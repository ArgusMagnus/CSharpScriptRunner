using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Terminal.Gui;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static async Task DoRepl()
        {
            using var scope = new SynchronizationContextScope();
            await scope.Install(null);
            Application.Init();
            Application.Top.Add(new ReplApp());
            Application.Run();
        }

        sealed class ReplApp : Window
        {
            readonly TextView _ctrlHistory;
            readonly TextField _ctrlInput;
            readonly ListView _ctrlIntellisense;
            Script<object> _script;
            bool _isProcessing = false;

            public ReplApp()
            : base($"{nameof(CSharpScriptRunner)} REPL")
            {
                _ctrlHistory = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true };

                Label labelInput = new() { X = 0, Y = Pos.Bottom(_ctrlHistory), Height = 1, Text = "> " };

                _ctrlInput = new() { X = Pos.Right(labelInput), Y = Pos.Bottom(_ctrlHistory), Width = Dim.Fill(), Height = 1 };

                _ctrlIntellisense = new() { X = 0, Y = 0, Visible = false };
                _ctrlIntellisense.SetSource(new[] { "Item 1", "Item 2", "Item 3" });

                Add(_ctrlHistory, labelInput, _ctrlInput, _ctrlIntellisense);
                _ctrlInput.SetFocus();

                _ctrlHistory.KeyPress += OnKeyPressHistory;
                _ctrlInput.KeyPress += OnKeyPressInput;

                _script = CSharpScript.Create(string.Empty);
            }

            void OnKeyPressHistory(View.KeyEventEventArgs args)
            {
                if (args.KeyEvent.Key == Key.Tab)
                {
                    args.Handled = true;
                    _ctrlInput.SetFocus();
                }
            }

            async void OnKeyPressInput(View.KeyEventEventArgs args)
            {
                if (args.KeyEvent.Key == Key.Enter)
                {
                    if (_ctrlInput.Text.IsEmpty || _isProcessing)
                        return;

                    _isProcessing = true;
                    var code = _ctrlInput.Text.ToString();
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
                    }

                    output.Add(string.Empty);
                    _ctrlHistory.Text += string.Join(Environment.NewLine, output);
                    _ctrlHistory.MoveEnd();
                    _ctrlInput.Text = string.Empty;
                    _isProcessing = false;
                }
            }
        }
    }
}