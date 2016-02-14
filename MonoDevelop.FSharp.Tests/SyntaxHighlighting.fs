namespace MonoDevelopTests

open System
open Microsoft.FSharp.Compiler.SourceCodeServices
open NUnit.Framework
open MonoDevelop.FSharp
open Mono.TextEditor
open Mono.TextEditor.Highlighting
open MonoDevelop.Ide.Editor
open FsUnit

[<TestFixture>]
type SyntaxHighlighting() =
    let getMarkup (input:string) =
        let offset = input.IndexOf("|")
        let length = input.LastIndexOf("|") - offset
        let input = input.Replace("|", "")
        let data = new TextEditorData (new TextDocument (input))
        //data.Document.SyntaxMode <- SyntaxModeService.GetSyntaxMode (data.Document, "text/x-fsharp")
        //data.ColorStyle <- SyntaxModeService.GetColorStyle ("TangoLight")
        //data.GetMarkup (0, data.Length, false)

        let syntaxMode = SyntaxModeService.GetSyntaxMode (data.Document, "text/x-fsharp")
        let style = SyntaxModeService.GetColorStyle ("Gruvbox")
        let line = data.Lines |> Seq.head
        let chunk = syntaxMode.GetChunks(style, line, offset, length) |> Seq.head
        printfn "%A" chunk

    [<TestCase("let simpleBinding = §1§", "Number")>]
    member x.``Syntax highlighting``(source, expectedStyle) =
        getMarkup source