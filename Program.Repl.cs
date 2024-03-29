using System.Threading.Tasks;
using Terminal.Gui;

namespace CSharpScriptRunner
{
    static partial class Program
    {
        static void DoRepl()
        {
            using var scope = new SynchronizationContextScope();
            scope.Install(null);
            Application.Init();
            Application.Top.Add(new ReplApp());
            Application.Run();
        }
    }
}