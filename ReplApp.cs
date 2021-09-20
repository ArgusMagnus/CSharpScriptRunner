using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;
using Terminal.Gui;

namespace CSharpScriptRunner
{
    sealed class ReplApp : Window
    {
        const int MaxIntellisenseHeight = 10;
        readonly TextView _ctrlHistory;
        readonly TextField _ctrlInput;
        readonly ListView _ctrlIntellisense;
        Script<object> _script;
        string _scriptCode = string.Empty;
        bool _isProcessing = false;
        Document _doc;
        AdhocWorkspace _ws;
        ProjectId _projectId;
        int _idxIntellisense;

        public ReplApp()
        : base($"{nameof(CSharpScriptRunner)} REPL, {BuildInfo.ReleaseTag}")
        {
            ColorScheme = new()
            {
                Normal = new(Color.White, Color.Black),
                Focus = new(Color.White, Color.Black),
                Disabled = new(Color.White, Color.Black),
                HotNormal = new(Color.White, Color.Black),
                HotFocus = new(Color.White, Color.Black)
            };

            Label labelInput = new() { X = 0, Y = Pos.AnchorEnd() - 1, Height = 1, Text = "> " };
            _ctrlInput = new() { X = Pos.Right(labelInput), Y = Pos.AnchorEnd() - 1, Width = Dim.Fill(), Height = 1 };

            _ctrlHistory = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - Dim.Height(_ctrlInput), ReadOnly = true };

            _ctrlIntellisense = new() { X = 0, Y = 0, Height = MaxIntellisenseHeight, Visible = false };
            _ctrlIntellisense.ColorScheme = new()
            {
                Normal = new(Color.White, Color.DarkGray),
                HotNormal = new(Color.White, Color.Blue),
                Focus = new(Color.White, Color.DarkGray),
                HotFocus = new(Color.White, Color.Blue)
            };

            Add(_ctrlHistory, _ctrlIntellisense, labelInput, _ctrlInput);

            _ctrlInput.SetFocus();

            _ctrlHistory.KeyPress += OnKeyPressHistory;
            _ctrlInput.KeyPress += OnKeyPressInput;
            _ctrlInput.KeyUp += OnKeyUpInput;
            _ctrlInput.TextChanged += OnTextChangedInput;

            _script = CSharpScript.Create(string.Empty);

            // https://www.strathweb.com/2018/12/using-roslyn-c-completion-service-programmatically/
            var host = MefHostServices.DefaultHost;
            _ws = new AdhocWorkspace(host);
            _projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(_projectId, VersionStamp.Default, "Script", "Script", LanguageNames.CSharp, isSubmission: true)
                .WithCompilationOptions(_script.GetCompilation().Options)
                .WithMetadataReferences(_script.GetCompilation().References);

            var project = _ws.AddProject(projectInfo);
            var docInfo = DocumentInfo.Create(DocumentId.CreateNewId(_projectId), "Script", sourceCodeKind: SourceCodeKind.Script);
            _doc = _ws.AddDocument(docInfo);
        }

        void OnKeyPressHistory(View.KeyEventEventArgs args)
        {
            if (args.KeyEvent.Key == Key.Tab)
            {
                args.Handled = true;
                _ctrlInput.SetFocus();
            }
        }

        void OnKeyPressInput(View.KeyEventEventArgs args)
        {
            if (_ctrlIntellisense.Visible)
            {
                switch (args.KeyEvent.Key)
                {
                    default: return;

                    case Key.CursorDown:
                        if (_ctrlIntellisense.SelectedItem == _ctrlIntellisense.Source.Count - 1)
                            _ctrlIntellisense.MoveHome();
                        else
                            _ctrlIntellisense.MoveDown();
                        break;

                    case Key.CursorUp:
                        if (_ctrlIntellisense.SelectedItem == 0)
                            _ctrlIntellisense.MoveEnd();
                        else
                            _ctrlIntellisense.MoveUp();
                        break;

                    case Key.Tab:
                        var item = _ctrlIntellisense.Source.ToList()[_ctrlIntellisense.SelectedItem].ToString();
                        var text = _ctrlInput.Text.ToString();
                        var cursorsPos = text.Length + item.Length;
                        text = text.Substring(0, _idxIntellisense) + item;
                        _ctrlInput.Text = text;
                        _ctrlInput.CursorPosition = cursorsPos;
                        _ctrlIntellisense.Visible = false;
                        break;
                }

                _ctrlIntellisense.SetNeedsDisplay();
                args.Handled = true;
            }
        }

        async void OnKeyUpInput(View.KeyEventEventArgs args)
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                if (_ctrlInput.Text.IsEmpty || _isProcessing)
                    return;

                _isProcessing = true;
                var code = _ctrlInput.Text.ToString().TrimEnd();
                var newScript = _script.ContinueWith(code);
                var diagnostics = newScript.Compile();

                var output = new List<string>();
                output.Add($"> {code}");
                var success = true;
                foreach (var diag in diagnostics)
                {
                    if (diag.Severity == DiagnosticSeverity.Error)
                        success = false;
                    var loc = diag.Location.GetLineSpan();
                    output.Add($"{diag.Severity} ({loc.StartLinePosition.Line}, {loc.StartLinePosition.Character}): {diag.GetMessage()}");
                }

                if (success)
                {
                    var result = await newScript.RunAsync();
                    if (result.ReturnValue is string value)
                        value = SymbolDisplay.FormatLiteral(value, true);
                    else
                        value = result.ReturnValue?.ToString();
                    if (!string.IsNullOrEmpty(value))
                        output.Add(value);
                    _script = newScript;
                    if (!code.EndsWith(';'))
                        code += ';';
                    _scriptCode += code;
                }

                output.Add(string.Empty);
                _ctrlHistory.Text += string.Join(Environment.NewLine, output);
                _ctrlHistory.MoveEnd();
                _ctrlInput.Text = string.Empty;
                _isProcessing = false;
            }
            else if (args.KeyEvent.Key == Key.CursorLeft || args.KeyEvent.Key == Key.CursorRight)
            {
                OnTextChangedInput(_ctrlInput.Text);
            }
        }

        async void OnTextChangedInput(NStack.ustring oldText)
        {
            if (_ctrlInput.Text.IsEmpty)
            {
                _ctrlIntellisense.Visible = false;
                return;
            }

            var code = _scriptCode + _ctrlInput.Text.ToString();
            var doc = _doc.WithText(SourceText.From(code));
            var service = CompletionService.GetService(doc);
            var completion = await service.GetCompletionsAsync(doc, Math.Min(_ctrlInput.CursorPosition + _scriptCode.Length, code.Length));
            if (completion == null)
            {
                _ctrlIntellisense.Visible = false;
                return;
            }

            var items = completion.Items;
            if (items != null)
            {
                var filter = code.Substring(0, Math.Min(_ctrlInput.CursorPosition + _scriptCode.Length, code.Length));
                var idx = filter.LastIndexOfAny(completion.Rules.DefaultCommitCharacters.ToArray());
                if (idx > -1)
                    filter = filter.Substring(idx);
                items = service.FilterItems(doc, items, filter);
                _idxIntellisense = idx + 1 - _scriptCode.Length;
            }

            if (items.Length == 0)
                _ctrlIntellisense.Visible = false;
            else
            {
                var completionItems = items.Select(x => x.DisplayText).ToList();
                _ctrlIntellisense.SetSource(completionItems);
                _ctrlIntellisense.X = Pos.Left(_ctrlInput) + _idxIntellisense;


                _ctrlIntellisense.Y = Pos.Top(_ctrlInput) - Math.Min(completionItems.Count, MaxIntellisenseHeight);
                _ctrlIntellisense.Width = _ctrlIntellisense.Maxlength;
                // _ctrlIntellisense.Height = height;
                _ctrlIntellisense.Visible = true;
            }
        }
    }
}