using BenchmarkDotNet.Running;

namespace OpenRA.Benchmark
{
	sealed class Program
	{
		static void Main()
		{
			BenchmarkRunner.Run(typeof(Program).Assembly);
		}
	}
}
