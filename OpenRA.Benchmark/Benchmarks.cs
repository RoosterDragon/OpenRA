using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;
using OpenRA.Primitives;
using OpenRA.Support;

#pragma warning disable CA1515 // Consider making public types internal
namespace OpenRA.Benchmark
{
	// For more information on the VS BenchmarkDotNet Diagnosers see https://learn.microsoft.com/visualstudio/profiling/profiling-with-benchmark-dotnet
	[CPUUsageDiagnoser]
	//[DotNetObjectAllocJobConfiguration]
	//[DotNetObjectAllocDiagnoser]
	[MemoryDiagnoser]
	public class Benchmarks
	{
		MiniYaml yaml;

		[GlobalSetup]
		public void Setup()
		{
			yaml = new MiniYaml(
				null,
				[
					new MiniYamlNode(nameof(LoadTarget.Int), "123"),
					new MiniYamlNode(nameof(LoadTarget.Byte), "123"),
					new MiniYamlNode(nameof(LoadTarget.Short), "123"),
					new MiniYamlNode(nameof(LoadTarget.UShort), "123"),
					new MiniYamlNode(nameof(LoadTarget.Float), "123.4"),
					new MiniYamlNode(nameof(LoadTarget.Decimal), "123.4"),
					new MiniYamlNode(nameof(LoadTarget.String), "test"),
					new MiniYamlNode(nameof(LoadTarget.Color), Color.CornflowerBlue.ToString()),
					new MiniYamlNode(nameof(LoadTarget.Hotkey), new Hotkey(Keycode.A, Modifiers.Shift).ToString()),
					new MiniYamlNode(nameof(LoadTarget.WDist), "123"),
					new MiniYamlNode(nameof(LoadTarget.WVec), "123,456,789"),
					new MiniYamlNode(nameof(LoadTarget.WVecArray), "123,456,789"),
					new MiniYamlNode(nameof(LoadTarget.WPos), "123,456,789"),
					new MiniYamlNode(nameof(LoadTarget.WAngle), "123"),
					new MiniYamlNode(nameof(LoadTarget.WRot), "123,456,789"),
					new MiniYamlNode(nameof(LoadTarget.CPos), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.CPosArray), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.CVec), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.CVecArray), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.BooleanExpression), "true"),
					new MiniYamlNode(nameof(LoadTarget.IntegerExpression), "1 + 2"),
					new MiniYamlNode(nameof(LoadTarget.Enum), MapGridType.RectangularIsometric.ToString()),
					new MiniYamlNode(nameof(LoadTarget.Bool), "true"),
					new MiniYamlNode(nameof(LoadTarget.Int2Array), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.Size), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.Int2), "123,456"),
					new MiniYamlNode(nameof(LoadTarget.Float2), "123.4,567.8"),
					new MiniYamlNode(nameof(LoadTarget.Float3), "123.4,567.8,9.01"),
					new MiniYamlNode(nameof(LoadTarget.Rectangle), "123, 456, 789, 123"),
					new MiniYamlNode(nameof(LoadTarget.DateTime), new DateTime(2000, 1, 1).ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)),
					new MiniYamlNode(nameof(LoadTarget.Array), "1,2,3"),
					new MiniYamlNode(nameof(LoadTarget.List), "1,2,3"),
					new MiniYamlNode(nameof(LoadTarget.HashSet), "1,2,3"),
					new MiniYamlNode(nameof(LoadTarget.ImmutableArray), "1,2,3"),
					new MiniYamlNode(nameof(LoadTarget.FrozenSet), "1,2,3"),
					new MiniYamlNode(nameof(LoadTarget.BitSet), "a,b,c"),
					new MiniYamlNode(nameof(LoadTarget.Nullable), "1"),
					new MiniYamlNode(nameof(LoadTarget.TypeConverter), "1"),
					new MiniYamlNode(
						nameof(LoadTarget.Dictionary),
						new MiniYaml(
							null,
							[
								new MiniYamlNode("a", "12"),
								new MiniYamlNode("b", "34")
							])),
					new MiniYamlNode(
						nameof(LoadTarget.NestedDictionary),
						new MiniYaml(
							null,
							[
								new MiniYamlNode("a", "12,34"),
								new MiniYamlNode("b", "56,78")
							])),
					new MiniYamlNode(
						nameof(LoadTarget.DoubleNestedDictionary),
						new MiniYaml(
							null,
							[
								new MiniYamlNode("a", new MiniYaml(null, [new MiniYamlNode("a1", "1,2"), new MiniYamlNode("a2", "3,4")])),
								new MiniYamlNode("b", new MiniYaml(null, [new MiniYamlNode("b1", "5,6"), new MiniYamlNode("b2", "7,8")])),
							])),
					new MiniYamlNode(
						nameof(LoadTarget.NestedDictionaryWithMatchingKeyAndInnerValueType),
						new MiniYaml(
							null,
							[
								new MiniYamlNode("a", "12,34"),
								new MiniYamlNode("b", "56,78")
							])),
					new MiniYamlNode(
						nameof(LoadTarget.FrozenDictionary),
						new MiniYaml(
							null,
							[
								new MiniYamlNode("12", "34"),
								new MiniYamlNode("56", "78")
							])),
				]);
			var target = new LoadTarget() { Unset = "unset" };
			FieldLoader.Load(target, yaml);
		}

		[Benchmark]
		public LoadTarget FieldLoader_Init()
		{
			var target = new LoadTarget() { Unset = "unset" };
			FieldLoader.ClassLoadDelegates.Clear(); // This bench will include the one-time cost to generate the delegate.
			FieldLoader.Load(target, yaml);
			return target;
		}

		[Benchmark]
		public LoadTarget FieldLoader_Load()
		{
			var target = new LoadTarget() { Unset = "unset" };
			FieldLoader.Load(target, yaml);
			return target;
		}
	}

	public sealed class LoadTarget
	{
		public string Unset;
		public int Int;
		public byte Byte;
		public short Short;
		public ushort UShort;
		public float Float;
		public decimal Decimal;
		public string String;
		public Color Color;
		public Hotkey Hotkey;
		public HotkeyReference HotkeyReference;
		public WDist WDist;
		public WVec WVec;
		public WVec[] WVecArray;
		public WPos WPos;
		public WAngle WAngle;
		public WRot WRot;
		public CPos CPos;
		public CPos[] CPosArray;
		public CVec CVec;
		public CVec[] CVecArray;
		public BooleanExpression BooleanExpression;
		public IntegerExpression IntegerExpression;
		public MapGridType Enum;
		public bool Bool;
		public int2[] Int2Array;
		public Size Size;
		public int2 Int2;
		public float2 Float2;
		public float3 Float3;
		public Rectangle Rectangle;
		public DateTime DateTime;
		public int[] Array;
		public List<int> List;
		public HashSet<int> HashSet;
		public ImmutableArray<int> ImmutableArray;
		public FrozenSet<int> FrozenSet;
		public BitSet<LoadTarget> BitSet;
		public int? Nullable;
		public sbyte TypeConverter;
		public Dictionary<string, int> Dictionary;
		public Dictionary<string, int[]> NestedDictionary;
		public Dictionary<string, Dictionary<string, int[]>> DoubleNestedDictionary;
		public Dictionary<string, string[]> NestedDictionaryWithMatchingKeyAndInnerValueType;
		public FrozenDictionary<int, int> FrozenDictionary;
	}
}
