// Run with CSharpScriptRunner: https://github.com/ArgusMagnus/CSharpScriptRunner/releases
//
// Arguments:
// Arguments are received in the global variable 'Args' which is of type 'IReadOnlyDictionary<string, string>'.
// Arguments are passed to the script in the format "-argName argValue". When no 'argValue' is specified,
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

using System;
using System.Collections.Generic;

Script.WriteLine("Hallo from script");

Script.WriteLine("Arguments:");
foreach (var arg in Args)
    Script.WriteLine($"{arg.Key}: {arg.Value}");

Script.WriteLine("Press Enter to exit");
Console.ReadLine();

#region Utilities

public static class Script
{
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
}

#endregion