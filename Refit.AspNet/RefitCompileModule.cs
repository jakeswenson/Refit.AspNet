using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Roslyn;
using System.IO;

namespace Refit.AspNet
{
    public abstract class RefitCompileModule : ICompileModule
    {
        private readonly IApplicationEnvironment _environment;
        private bool _produceTree = false;

        public RefitCompileModule(IApplicationEnvironment environment)
        {
            _environment = environment;
        }

        public RefitCompileModule(IApplicationEnvironment environment, bool produceTree) : this(environment)
        {
            _produceTree = produceTree;
        }

        public void AfterCompile(IAfterCompileContext context)
        {
        }

        public void BeforeCompile(IBeforeCompileContext context)
        {
            InterfaceStubGenerator stubGenerator = new InterfaceStubGenerator();
            var result = stubGenerator.GenerateInterfaceStubs(context.Compilation.SyntaxTrees);
            foreach (var diagnostic in result.Item2)
            {
                context.Diagnostics.Add(diagnostic);
            }

            if (_produceTree)
            {
                var generationFolder = Path.Combine(context.ProjectContext.ProjectDirectory, "Generated", "Refit");
                Directory.CreateDirectory(generationFolder);
                File.WriteAllText(Path.Combine(generationFolder, "tree.cs"), result.Item1.GetRoot().ToString());
            }
            else
            {
                context.Compilation = context.Compilation.AddSyntaxTrees(result.Item1);
            }
        }
    }
}
