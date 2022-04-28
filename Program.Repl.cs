using System.Threading.Tasks;
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
    }
}