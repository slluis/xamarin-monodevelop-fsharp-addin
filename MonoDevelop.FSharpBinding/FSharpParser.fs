﻿namespace MonoDevelop.FSharp

open ICSharpCode.NRefactory.TypeSystem
open Microsoft.FSharp.Compiler
open MonoDevelop.Core
open MonoDevelop.Ide
open MonoDevelop.Ide.Editor
open MonoDevelop.Ide.TypeSystem
open System
open System.Collections.Generic
open System.IO
open System.Threading

type FSharpParsedDocument(fileName) =
    inherit DefaultParsedDocument(fileName,Flags = ParsedDocumentFlags.NonSerializable)
    member val Tokens = None with get,set
    member val AllSymbolsKeyed = Dictionary<_,_>() with get, set
    
[<AutoOpen>]
module DocumentContextExt =
    open MonoDevelop
    type DocumentContext with
        member x.TryGetFSharpParsedDocumentTokens() =
            x.TryGetParsedDocument()
            |> Option.bind (function :? FSharpParsedDocument as fpd -> fpd.Tokens | _ -> None)
            
module ParsedDocument =
    /// Format errors for the given line (if there are multiple, we collapse them into a single one)
    let private formatError (error : FSharpErrorInfo) =
      // Single error for this line
        let errorType =
            if error.Severity = FSharpErrorSeverity.Error then ErrorType.Error
            else ErrorType.Warning
        Error(errorType, String.wrapText error.Message 80, DocumentRegion (error.StartLineAlternate, error.StartColumn + 1, error.EndLineAlternate, error.EndColumn + 1))
        
    let create (parseOptions: ParseOptions) (parseResults: ParseAndCheckResults) defines =
      //Try creating tokens
        async {
            let fileName = parseOptions.FileName
            let shortFilename = Path.GetFileName fileName
            let doc = new FSharpParsedDocument(fileName, Flags = ParsedDocumentFlags.NonSerializable)
            LoggingService.LogDebug ("FSharpParser: Processing tokens on {0}", shortFilename)
            try
                let readOnlyDoc = TextEditorFactory.CreateNewReadonlyDocument (parseOptions.Content, fileName)
                let lineDetails =
                  [ for i in 1..readOnlyDoc.LineCount do
                        let line = readOnlyDoc.GetLine(i)
                        yield Tokens.LineDetail(line.LineNumber, line.Offset, readOnlyDoc.GetTextAt(line.Offset, line.Length)) ]
                let tokens = Tokens.getTokens lineDetails fileName defines
                doc.Tokens <- Some(tokens)
                LoggingService.LogDebug ("FSharpParser: Tokens processed for: {0}", shortFilename)
            with ex ->
              LoggingService.LogWarning ("FSharpParser: Couldn't update token information", ex)

            //Get all the symboluses now rather than in semantic highlighting
            LoggingService.LogDebug ("FSharpParser: Processing symbol uses on {0}", shortFilename)

            parseResults.GetErrors() |> (Seq.map formatError >> doc.AddRange)
            let! allSymbolUses = parseResults.GetAllUsesOfAllSymbolsInFile()
            allSymbolUses |> Option.iter (fun symbolUses ->
                for symbolUse in symbolUses do
                    if not (doc.AllSymbolsKeyed.ContainsKey symbolUse.RangeAlternate.End)
                    then doc.AllSymbolsKeyed.Add(symbolUse.RangeAlternate.End, symbolUse))

            //Set code folding regions, GetNavigationItems may throw in some situations
            LoggingService.LogDebug ("FSharpParser: processing regions on {0}", shortFilename)
            try
                let regions =
                    let processDecl (decl : SourceCodeServices.FSharpNavigationDeclarationItem) =
                        let m = decl.Range
                        FoldingRegion(decl.Name, DocumentRegion(m.StartLine, m.StartColumn + 1, m.EndLine, m.EndColumn + 1))

                    seq { for toplevel in parseResults.GetNavigationItems() do
                              yield processDecl toplevel.Declaration
                              for next in toplevel.Nested do
                                  yield processDecl next }
                regions |> doc.AddRange
            with ex -> LoggingService.LogWarning ("FSharpParser: Couldn't update navigation items.", ex)
            //Store the AST of active results
            doc.Ast <- parseResults
            doc.LastWriteTimeUtc <- try File.GetLastWriteTimeUtc(fileName) with _ -> DateTime.UtcNow
            return doc :> ParsedDocument
        }

// An instance of this type is created by MonoDevelop (as defined in the .xml for the AddIn)
type FSharpParser() =
    inherit TypeSystemParser()

    let tryGetFilePath fileName (project: MonoDevelop.Projects.Project) =
        // TriggerParse will work only for full paths
        if IO.Path.IsPathRooted(fileName) then Some(fileName)
        else
            let workBench = IdeApp.Workbench
            match workBench with
            | null ->
                let filePaths = project.GetItemFiles(true)
                let res = filePaths |> Seq.find(fun t -> t.FileName = fileName)
                Some(res.FullPath.ToString())
            | wb ->
                match wb.ActiveDocument with
                | null -> None
                | doc -> let file = doc.FileName.FullPath.ToString()
                         if file = "" then None else Some file

    let isObsolete filename version (token:CancellationToken) =
        SourceCodeServices.IsResultObsolete(fun () ->
            let shortFilename = Path.GetFileName filename
            try
                if not token.IsCancellationRequested then
                    match MonoDevelop.tryGetVisibleDocument filename with
                    | Some doc ->
                        let newVersion = doc.Editor.Version
                        if newVersion.BelongsToSameDocumentAs(version) && newVersion.CompareAge(version) = 0 then
                            false
                        else
                            LoggingService.LogDebug ("FSharpParser: Parse {0} is obsolete type check cancelled, file has changed", shortFilename)
                            true
                    | None ->
                        LoggingService.LogDebug ("FSharpParser: Parse {0} is obsolete type check cancelled, file no longer visible", shortFilename)
                        true
                else
                    LoggingService.LogDebug ("FSharpParser: Parse {0} is obsolete type check cancelled by cancellationToken", shortFilename)
                    true
            with ex ->
                LoggingService.LogDebug ("FSharpParser: Parse {0} unable to determine cancellation due to exception", shortFilename, ex)
                false )
                
    override x.Parse(parseOptions, cancellationToken) =
        let fileName = parseOptions.FileName
        let content = parseOptions.Content

        let proj = parseOptions.Project

        let doc = MonoDevelop.tryGetVisibleDocument fileName

        if doc.IsNone || not (FileService.supportedFileName (fileName)) then null else

        let shortFilename = Path.GetFileName fileName
        LoggingService.LogDebug ("FSharpParser: Parse starting on {0}", shortFilename)

        Async.StartAsTask(
            cancellationToken = cancellationToken,
            computation =
                async {
                    match tryGetFilePath fileName proj with
                    | Some filePath ->
                        LoggingService.LogDebug ("FSharpParser: Running ParseAndCheckFileInProject for {0}", shortFilename)
                        let projectFile = proj |> function null -> filePath | proj -> proj.FileName.ToString()
                        let obsolete = isObsolete parseOptions.FileName parseOptions.Content.Version cancellationToken
                        try
                            let! pendingParseResults = Async.StartChild(languageService.ParseAndCheckFileInProject(projectFile, filePath, 0, content.Text, obsolete), ServiceSettings.maximumTimeout)
                            LoggingService.LogDebug ("FSharpParser: Parse and check results retieved on {0}", shortFilename)
                            let defines = CompilerArguments.getDefineSymbols filePath (proj |> Option.ofNull)
                            let! results = pendingParseResults
                            //if you ever want to see the current parse tree
                            //let pt = match results.ParseTree with Some pt -> sprintf "%A" pt | _ -> ""
                            return! ParsedDocument.create parseOptions results defines
                        with exn ->
                            LoggingService.LogError ("FSharpParser: Error ParsedDocument on {0}", shortFilename, exn)
                            return FSharpParsedDocument(fileName) :> _
                    | None -> return FSharpParsedDocument(fileName) :> _ })