// Run with CSharpScriptRunner: https://github.com/ArgusMagnus/CSharpScriptRunner/releases
//
// Arguments:
// Arguments are received in the global variable 'Args' which is of type 'string[]'.
// The Script.ParseArguments method expects that the arguments were passed to
// the script in the format "-argName argValue". When no 'argValue' is specified,
// the value is assumed to be "True".
// Example: CSharpScriptRunner.exe Script.csx -switch1 -arg1 Hello -arg2 World
//
// References:
// .NET assemblies can be referenced with the '#r "AssemblyName"' directive, for example:
// #r "PresentationFramework"
// #r "System.Windows.Forms"
// References to NuGet packages can be added by adding a line which matches the Regex:
// ^//#AddPackage\s+"(?<name>[\w.-]+),\s*(?<version>[\w.-]+)"\s*$
// e.g. //#AddPackage "System.Threading.Thread, 4.3.0"

#r "PresentationFramework"

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Script.HideConsole();

Script.WriteLine($"Executing script '{Script.ScriptPath}'...", ConsoleColor.Green);
Script.WriteLine("Hallo from script");

Script.WriteLine("Arguments:");
Script.WriteLines(Script.ParseArguments(Args).Select(x => $"{x.Key}: {x.Value}"));

var window = (Window)XamlReader.Parse(@"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    SizeToContent=""WidthAndHeight""
    MinWidth=""300""
    Title=""My Window"">
	<StackPanel HorizontalAlignment=""Center"" VerticalAlignment=""Center"" Margin=""20"">
        <Button Content=""Test"" Command=""{Binding}"" Padding=""5"" />
    </StackPanel>
</Window>");

window.DataContext = new Command(() => MessageBox.Show("Hello World!"));
window.ShowDialog();

#region Utilities

public static class Script
{
	static string GetScriptPath([System.Runtime.CompilerServices.CallerFilePath] string path = null) => path;
	public static string ScriptPath { get; } = GetScriptPath();
	public static string ScriptDirectory { get; } = System.IO.Path.GetDirectoryName(ScriptPath);
	public static string ScriptFilename { get; } = System.IO.Path.GetFileName(ScriptPath);

	static readonly IntPtr _consoleWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	const int SW_HIDE = 0;
	const int SW_SHOW = 5;

	public static void HideConsole() { IsConsoleHidden = true; ShowWindow(_consoleWindow, SW_HIDE); }
	public static void ShowConsole() { IsConsoleHidden = false; ShowWindow(_consoleWindow, SW_SHOW); }
	public static bool IsConsoleHidden { get; private set; } = false;

	public static void WriteLine(string text, ConsoleColor color = (ConsoleColor)(-1))
	{
		var c = Console.ForegroundColor;
		if ((int)color > -1)
			Console.ForegroundColor = color;
		Console.WriteLine(text);
		Console.ForegroundColor = c;
	}

    public static void WriteLines(IEnumerable<string> lines, ConsoleColor color = (ConsoleColor)(-1))
    {
        var c = Console.ForegroundColor;
        if ((int)color > -1)
            Console.ForegroundColor = color;
        foreach (var line in lines)
            Console.WriteLine(line);
        Console.ForegroundColor = c;
    }

    public static void Write(string text, ConsoleColor color = (ConsoleColor)(-1))
	{
		var c = Console.ForegroundColor;
		if ((int)color > -1)
			Console.ForegroundColor = color;
		Console.Write(text);
		Console.ForegroundColor = c;
	}

	public static IDictionary<string, string> ParseArguments(IEnumerable<string> args)
	{
		var arguments = new Dictionary<string, string>();
		string key = null;
		foreach (var value in args)
		{
			if (value.StartsWith("-"))
			{
				if (key != null)
					arguments[key] = true.ToString();
				key = value.Substring(1);
			}
			else
			{
				if (key == null)
					throw new ArgumentNullException(value, "The parameter is missing its name.");
				arguments[key] = value;
				key = null;
			}
		}
		if (key != null)
			arguments[key] = true.ToString();
		return arguments;
	}
}

#region WPF utility classes

public abstract class NotifyPropertyChanged : INotifyPropertyChanged
{
	string[] _propertyNames;

	PropertyChangedEventHandler _propertyChangedHandler;
	event PropertyChangedEventHandler INotifyPropertyChanged.PropertyChanged
	{
		add => _propertyChangedHandler += value;
		remove => _propertyChangedHandler += value;
	}

	protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) => OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

	protected virtual void OnPropertyChanged(PropertyChangedEventArgs args)
	{
		_propertyChangedHandler?.Invoke(this, args);
	}

	protected void RaisePropertyChangedForAll(Predicate<string> filter = null)
	{
		if (_propertyNames == null)
			_propertyNames = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name).ToArray();
		foreach (var prop in _propertyNames)
		{
			if (filter?.Invoke(prop) ?? true)
				RaisePropertyChanged(prop);
		}
	}

	/// <summary>
	/// All deriving classes should set their properties using this method.
	/// This ensures that the <see cref="INotifyPropertyChanged.PropertyChanged"/> event
	/// is properly raised and data binding works.
	/// </summary>
	protected void SetProperty<T>(ref T backingField, T value, Action<T, T> propertyChangedHandler = null, bool setOnlyIfChanged = true, [CallerMemberName] string propertyName = null)
		=> SetProperty(ref backingField, value, RaisePropertyChanged, propertyChangedHandler, propertyName);

	public static bool SetProperty<T>(ref T backingField, T value, Action<string> propertyChangedEvent, Action<T, T> propertyChangedHandler = null, [CallerMemberName] string propertyName = null)
	{
		var equatable = backingField as IEquatable<T>;
		bool isEqual;
		if (equatable != null)
			isEqual = equatable?.Equals(value) ?? object.ReferenceEquals(backingField, value);
		else
			isEqual = backingField?.Equals(value) ?? object.ReferenceEquals(backingField, value);
		var oldValue = backingField;
		backingField = value;
		if (!isEqual)
		{
			propertyChangedEvent?.Invoke(propertyName);
			propertyChangedHandler?.Invoke(oldValue, value);
		}
		return !isEqual;
	}
}

public sealed class Command : Command<object>
{
	public Command(Action execute, Func<bool> queryCanExecute = null)
		: base(par => execute(), queryCanExecute == null ? null : new Func<object, bool>(ignored => queryCanExecute())) { }
}

public class Command<TParameter> : NotifyPropertyChanged, ICommand
{
	readonly Action<TParameter> _execute;
	readonly Func<TParameter, bool> _queryCanExecute;
	int _isExecuting;
	bool _canExecute;

	public event EventHandler CanExecuteChanged;

	public Command(Action<TParameter> execute, Func<TParameter, bool> queryCanExecute = null)
	{
		_execute = execute;
		_queryCanExecute = queryCanExecute;
		_isExecuting = 0;
		_canExecute = _queryCanExecute?.Invoke(default) ?? true;
	}

	public bool CanExecute
	{
		get => _canExecute;
		set => SetProperty(ref _canExecute, value, (x, y) => CanExecuteChanged?.Invoke(this, null));
	}

	public bool QueryCanExecute(TParameter parameter = default)
	{
		if (Interlocked.CompareExchange(ref _isExecuting, default, default) != 0)
			return false;
		CanExecute = _queryCanExecute?.Invoke(parameter) ?? true;
		return CanExecute;
	}

	bool ICommand.CanExecute(object parameter) => QueryCanExecute(parameter != null ? (TParameter)parameter : default);

	void ICommand.Execute(object parameter)
	{
		if (parameter != null && !(parameter is TParameter))
			throw new ArgumentException($"Instance of type '{typeof(TParameter).FullName}' expected.", nameof(parameter));

		TParameter para = default;
		if (parameter != null)
			para = (TParameter)parameter;

		Execute(para);
	}

	public void Execute(TParameter parameter = default)
	{
		if (!CanExecute || Interlocked.Exchange(ref _isExecuting, 1) != 0)
			return;

		CanExecute = false;
		_execute(parameter);
		Interlocked.Exchange(ref _isExecuting, 0);
		CanExecute = _queryCanExecute?.Invoke(parameter) ?? true;
	}
}

public sealed class AsyncCommand : AsyncCommand<object>
{
	public AsyncCommand(Func<CancellationToken, Task> execute, Func<bool> queryCanExecute = null)
		: base((par, token) => execute(token), queryCanExecute == null ? null : new Func<object, bool>(ignored => queryCanExecute())) { }

	public AsyncCommand(Func<Task> execute, Func<bool> queryCanExecute = null)
		: base((par, token) => execute(), queryCanExecute == null ? null : new Func<object, bool>(ignored => queryCanExecute())) { }
}

public class AsyncCommand<TParameter> : NotifyPropertyChanged, ICommand
{
	readonly Func<TParameter, CancellationToken, Task> _execute;
	readonly Func<TParameter, bool> _queryCanExecute;
	CancellationTokenSource _cancellationTokenSource;
	int _isExecuting;
	bool _canExecute;

	public event EventHandler CanExecuteChanged;

	public AsyncCommand(Func<TParameter, CancellationToken, Task> execute, Func<TParameter, bool> queryCanExecute = null)
	{
		_execute = execute;
		_queryCanExecute = queryCanExecute;
		_isExecuting = 0;
		_canExecute = _queryCanExecute?.Invoke(default) ?? true;
	}

	public AsyncCommand(Func<TParameter, Task> execute, Func<TParameter, bool> queryCanExecute = null)
		: this((par, cancellationToke) => execute(par), queryCanExecute) { }

	public bool CanExecute
	{
		get => _canExecute;
		set => SetProperty(ref _canExecute, value, (x, y) => CanExecuteChanged?.Invoke(this, null));
	}

	public bool QueryCanExecute(TParameter parameter = default)
	{
		if (Interlocked.CompareExchange(ref _isExecuting, default, default) != 0)
			return false;
		CanExecute = _queryCanExecute?.Invoke(parameter) ?? true;
		return CanExecute;
	}

	bool ICommand.CanExecute(object parameter) => QueryCanExecute(parameter != null ? (TParameter)parameter : default);

	async void ICommand.Execute(object parameter)
	{
		if (parameter != null && !(parameter is TParameter))
			throw new ArgumentException($"Instance of type '{typeof(TParameter).FullName}' expected.", nameof(parameter));

		TParameter para = default;
		if (parameter != null)
			para = (TParameter)parameter;

		await Execute(para).ConfigureAwait(false);
	}

	public async Task Execute(TParameter parameter = default)
	{
		if (!CanExecute || Interlocked.Exchange(ref _isExecuting, 1) != 0)
			return;

		CanExecute = false;
		var cancellationSource = new CancellationTokenSource();
		Interlocked.Exchange(ref _cancellationTokenSource, cancellationSource);
		try { await _execute(parameter, cancellationSource.Token); }
		catch (TaskCanceledException) { }
		Interlocked.Exchange(ref _cancellationTokenSource, null);
		Interlocked.Exchange(ref _isExecuting, 0);
		CanExecute = _queryCanExecute?.Invoke(parameter) ?? true;
	}

	public void Cancel() => Interlocked.CompareExchange(ref _cancellationTokenSource, default, default)?.Cancel();
	public void CancelAfter(TimeSpan delay) => Interlocked.CompareExchange(ref _cancellationTokenSource, default, default)?.CancelAfter(delay);
	public bool IsCancellationRequested => Interlocked.CompareExchange(ref _cancellationTokenSource, default, default)?.IsCancellationRequested ?? false;
}

#endregion

#endregion