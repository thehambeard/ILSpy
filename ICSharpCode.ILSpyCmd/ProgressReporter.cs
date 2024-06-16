using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;

namespace ICSharpCode.ILSpyCmd
{
	internal class ProgressReporter : IProgress<DecompilationProgress>
	{
		public void Report(DecompilationProgress value)
		{
			Console.Write($"\rProgress: {value.UnitsCompleted} / {value.TotalUnits} {value.UnitsCompleted * 100 / value.TotalUnits}%");
		}
	}
}
