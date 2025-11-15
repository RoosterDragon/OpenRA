#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using OpenRA.Primitives;
using OpenRA.Support;

namespace OpenRA
{
	// TODO: Can we remove LoadFieldOrProperty in favour of calls to GetValue or similar??
	// TODO: ParseDictionaryRecursive can benefit from a 'flat' key to avoid allocing a MiniYaml obj.
	public static class FieldLoader
	{
		const char Comma = ',';

		public class MissingFieldsException : YamlException
		{
			public readonly string[] Missing;
			public readonly string Header;
			public override string Message
			{
				get
				{
					return (string.IsNullOrEmpty(Header) ? "" : Header + ": ") + Missing[0]
						+ string.Concat(Missing.Skip(1).Select(m => ", " + m));
				}
			}

			public MissingFieldsException(string[] missing, string header = null, string headerSingle = null)
				: base(null)
			{
				Header = missing.Length > 1 ? header : headerSingle ?? header;
				Missing = missing;
			}
		}

		public static Func<string, Type, string, object> InvalidValueAction = (s, t, f) =>
			throw new YamlException($"FieldLoader: Cannot parse `{s}` into `{f}.{t}`");

		public static Action<string, Type> UnknownFieldAction = (s, f) =>
			throw new NotImplementedException($"FieldLoader: Missing field `{s}` on `{f.Name}`");

		static readonly ConcurrentCache<Type, FieldLoadInfo[]> TypeLoadInfo =
			new(BuildTypeLoadInfo);
		static readonly ConcurrentCache<Type, Delegate> ClassLoadDelegates =
			new(BuildClassLoadDelegate);
		static readonly ConcurrentCache<Type, (Delegate ParseDelegate, ParseDelegateKind Kind, Parsers Parsers)> ParseDelegateCache =
			new(CacheParseDelegate);
		static readonly ConcurrentCache<string, BooleanExpression> BooleanExpressionCache =
			new(expression => new BooleanExpression(expression));
		static readonly ConcurrentCache<string, IntegerExpression> IntegerExpressionCache =
			new(expression => new IntegerExpression(expression));

		static int ParseInt(string fieldName, Type fieldType, string value)
		{
			if (Exts.TryParseInt32Invariant(value, out var res))
				return res;

			return (int)InvalidValueAction(value, fieldType, fieldName);
		}

		static byte ParseByte(string fieldName, Type fieldType, string value)
		{
			if (Exts.TryParseByteInvariant(value, out var res))
				return res;

			return (byte)InvalidValueAction(value, fieldType, fieldName);
		}

		static short ParseShort(string fieldName, Type fieldType, string value)
		{
			if (Exts.TryParseInt16Invariant(value, out var res))
				return res;

			return (short)InvalidValueAction(value, fieldType, fieldName);
		}

		static ushort ParseUShort(string fieldName, Type fieldType, string value)
		{
			if (Exts.TryParseUInt16Invariant(value, out var res))
				return res;

			return (ushort)InvalidValueAction(value, fieldType, fieldName);
		}

		static float ParseFloat(string fieldName, Type fieldType, string value)
		{
			if (Exts.TryParseFloatOrPercentInvariant(value, out var res))
				return res;

			return (float)InvalidValueAction(value, fieldType, fieldName);
		}

		static decimal ParseDecimal(string fieldName, Type fieldType, string value)
		{
			if (value != null && decimal.TryParse(value.Replace("%", ""), NumberStyles.Float, NumberFormatInfo.InvariantInfo, out var res))
				return res * (value.Contains('%') ? 0.01m : 1m);

			return (decimal)InvalidValueAction(value, fieldType, fieldName);
		}

		static string ParseString(string fieldName, Type fieldType, string value)
		{
			return value?.Trim();
		}

		static Color ParseColor(string fieldName, Type fieldType, string value)
		{
			if (Color.TryParse(value, out var color))
				return color;

			return (Color)InvalidValueAction(value, fieldType, fieldName);
		}

		static Hotkey ParseHotkey(string fieldName, Type fieldType, string value)
		{
			if (Hotkey.TryParse(value?.Trim(), out var res))
				return res;

			return (Hotkey)InvalidValueAction(value, fieldType, fieldName);
		}

		static HotkeyReference ParseHotkeyReference(string fieldName, Type fieldType, string value)
		{
			return Game.ModData.Hotkeys[value];
		}

		static WDist ParseWDist(string fieldName, Type fieldType, string value)
		{
			if (WDist.TryParse(value, out var res))
				return res;

			return (WDist)InvalidValueAction(value, fieldType, fieldName);
		}

		static WVec ParseWVec(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 3
					&& WDist.TryParse(parts[0], out var rx)
					&& WDist.TryParse(parts[1], out var ry)
					&& WDist.TryParse(parts[2], out var rz))
					return new WVec(rx, ry, rz);
			}

			return (WVec)InvalidValueAction(value, fieldType, fieldName);
		}

		static WVec[] ParseWVecArray(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

				if (parts.Length % 3 != 0)
					return (WVec[])InvalidValueAction(value, fieldType, fieldName);

				var vecs = new WVec[parts.Length / 3];

				for (var i = 0; i < vecs.Length; ++i)
				{
					if (WDist.TryParse(parts[3 * i], out var rx)
						&& WDist.TryParse(parts[3 * i + 1], out var ry)
						&& WDist.TryParse(parts[3 * i + 2], out var rz))
						vecs[i] = new WVec(rx, ry, rz);
					else
						return (WVec[])InvalidValueAction(value, fieldType, fieldName);
				}

				return vecs;
			}

			return (WVec[])InvalidValueAction(value, fieldType, fieldName);
		}

		static WPos ParseWPos(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 3
					&& WDist.TryParse(parts[0], out var rx)
					&& WDist.TryParse(parts[1], out var ry)
					&& WDist.TryParse(parts[2], out var rz))
					return new WPos(rx, ry, rz);
			}

			return (WPos)InvalidValueAction(value, fieldType, fieldName);
		}

		static WAngle ParseWAngle(string fieldName, Type fieldType, string value)
		{
			if (Exts.TryParseInt32Invariant(value, out var res))
				return new WAngle(res);

			return (WAngle)InvalidValueAction(value, fieldType, fieldName);
		}

		static WRot ParseWRot(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 3
					&& Exts.TryParseInt32Invariant(parts[0], out var rr)
					&& Exts.TryParseInt32Invariant(parts[1], out var rp)
					&& Exts.TryParseInt32Invariant(parts[2], out var ry))
					return new WRot(new WAngle(rr), new WAngle(rp), new WAngle(ry));
			}

			return (WRot)InvalidValueAction(value, fieldType, fieldName);
		}

		static CPos ParseCPos(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 3
					&& Exts.TryParseInt32Invariant(parts[0], out var x)
					&& Exts.TryParseInt32Invariant(parts[1], out var y)
					&& Exts.TryParseByteInvariant(parts[2], out var layer))
					return new CPos(x, y, layer);

				if (parts.Length == 2
					&& Exts.TryParseInt32Invariant(parts[0], out x)
					&& Exts.TryParseInt32Invariant(parts[1], out y))
					return new CPos(x, y);
			}

			return (CPos)InvalidValueAction(value, fieldType, fieldName);
		}

		static CPos[] ParseCPosArray(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

				if (parts.Length % 2 != 0)
					return (CPos[])InvalidValueAction(value, fieldType, fieldName);

				var vecs = new CPos[parts.Length / 2];
				for (var i = 0; i < vecs.Length; i++)
				{
					if (Exts.TryParseInt32Invariant(parts[2 * i], out var rx)
						&& Exts.TryParseInt32Invariant(parts[2 * i + 1], out var ry))
						vecs[i] = new CPos(rx, ry);
					else
						return (CPos[])InvalidValueAction(value, fieldType, fieldName);
				}

				return vecs;
			}

			return (CPos[])InvalidValueAction(value, fieldType, fieldName);
		}

		static CVec ParseCVec(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 2
					&& Exts.TryParseInt32Invariant(parts[0], out var x)
					&& Exts.TryParseInt32Invariant(parts[1], out var y))
					return new CVec(x, y);
			}

			return (CVec)InvalidValueAction(value, fieldType, fieldName);
		}

		static CVec[] ParseCVecArray(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

				if (parts.Length % 2 != 0)
					return (CVec[])InvalidValueAction(value, fieldType, fieldName);

				var vecs = new CVec[parts.Length / 2];
				for (var i = 0; i < vecs.Length; i++)
				{
					if (Exts.TryParseInt32Invariant(parts[2 * i], out var rx)
						&& Exts.TryParseInt32Invariant(parts[2 * i + 1], out var ry))
						vecs[i] = new CVec(rx, ry);
					else
						return (CVec[])InvalidValueAction(value, fieldType, fieldName);
				}

				return vecs;
			}

			return (CVec[])InvalidValueAction(value, fieldType, fieldName);
		}

		static BooleanExpression ParseBooleanExpression(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				try
				{
					return BooleanExpressionCache[value.Trim()];
				}
				catch (InvalidDataException e)
				{
					throw new YamlException($"FieldLoader: Cannot parse `{value}` into `{fieldName}.{fieldType}`: {e.Message}");
				}
			}

			return (BooleanExpression)InvalidValueAction(value, fieldType, fieldName);
		}

		static IntegerExpression ParseIntegerExpression(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				try
				{
					return IntegerExpressionCache[value.Trim()];
				}
				catch (InvalidDataException e)
				{
					throw new YamlException($"FieldLoader: Cannot parse `{value}` into `{fieldName}.{fieldType}`: {e.Message}");
				}
			}

			return (IntegerExpression)InvalidValueAction(value, fieldType, fieldName);
		}

		static T ParseEnum<T>(string fieldName, Type _, string value) where T : struct
		{
			// Will allow numeric values that fit the underlying type of the enum, even if they aren't defined enumeration members.
			if (Enum.TryParse<T>(value, true, out var enumValue))
			{
				return enumValue;
			}

			return (T)InvalidValueAction(value, typeof(T), fieldName);
		}

		static bool ParseBool(string fieldName, Type fieldType, string value)
		{
			if (bool.TryParse(value, out var result))
				return result;

			return (bool)InvalidValueAction(value, fieldType, fieldName);
		}

		static int2[] ParseInt2Array(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length % 2 != 0)
					return (int2[])InvalidValueAction(value, fieldType, fieldName);

				var ints = new int2[parts.Length / 2];

				for (var i = 0; i < ints.Length; i++)
				{
					if (Exts.TryParseInt32Invariant(parts[2 * i], out var x)
						&& Exts.TryParseInt32Invariant(parts[2 * i + 1], out var y))
						ints[i] = new int2(x, y);
					else
						return (int2[])InvalidValueAction(value, fieldType, fieldName);
				}

				return ints;
			}

			return (int2[])InvalidValueAction(value, fieldType, fieldName);
		}

		static Size ParseSize(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 2
					&& Exts.TryParseInt32Invariant(parts[0], out var width)
					&& Exts.TryParseInt32Invariant(parts[1], out var height))
					return new Size(width, height);
			}

			return (Size)InvalidValueAction(value, fieldType, fieldName);
		}

		static int2 ParseInt2(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 2
					&& Exts.TryParseInt32Invariant(parts[0], out var x)
					&& Exts.TryParseInt32Invariant(parts[1], out var y))
					return new int2(x, y);
			}

			return (int2)InvalidValueAction(value, fieldType, fieldName);
		}

		static float2 ParseFloat2(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 2
					&& Exts.TryParseFloatOrPercentInvariant(parts[0], out var x)
					&& Exts.TryParseFloatOrPercentInvariant(parts[1], out var y))
					return new float2(x, y);
			}

			return (float2)InvalidValueAction(value, fieldType, fieldName);
		}

		static float3 ParseFloat3(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 3
					&& Exts.TryParseFloatOrPercentInvariant(parts[0], out var x)
					&& Exts.TryParseFloatOrPercentInvariant(parts[1], out var y)
					&& Exts.TryParseFloatOrPercentInvariant(parts[2], out var z))
					return new float3(x, y, z);

				// z component is optional for compatibility with older float2 definitions
				if (parts.Length == 2
					&& Exts.TryParseFloatOrPercentInvariant(parts[0], out x)
					&& Exts.TryParseFloatOrPercentInvariant(parts[1], out y))
					return new float3(x, y, 0);
			}

			return (float3)InvalidValueAction(value, fieldType, fieldName);
		}

		static Rectangle ParseRectangle(string fieldName, Type fieldType, string value)
		{
			if (value != null)
			{
				var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 4
					&& Exts.TryParseInt32Invariant(parts[0], out var x)
					&& Exts.TryParseInt32Invariant(parts[1], out var y)
					&& Exts.TryParseInt32Invariant(parts[2], out var width)
					&& Exts.TryParseInt32Invariant(parts[3], out var height))
					return new Rectangle(x, y, width, height);
			}

			return (Rectangle)InvalidValueAction(value, fieldType, fieldName);
		}

		static DateTime ParseDateTime(string fieldName, Type fieldType, string value)
		{
			if (DateTime.TryParseExact(value.AsSpan().Trim(), "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
				return dt;

			return (DateTime)InvalidValueAction(value, fieldType, fieldName);
		}

		static T[] ParseArray<T>(string field, Type _, string value, Func<string, Type, string, T> parseInner)
		{
			if (value == null)
				return [];

			var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 0)
				return [];

			var ret = new T[parts.Length];
			for (var i = 0; i < parts.Length; i++)
				ret[i] = parseInner(field, typeof(T), parts[i]);
			return ret;
		}

		static List<T> ParseList<T>(string field, Type _, string value, Func<string, Type, string, T> parseInner)
		{
			if (value == null)
				return [];

			var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 0)
				return [];

			var ret = new List<T>(parts.Length);
			foreach (var part in parts)
				ret.Add(parseInner(field, typeof(T), part));
			return ret;
		}

		static HashSet<T> ParseHashSet<T>(string field, Type _, string value, Func<string, Type, string, T> parseInner)
		{
			if (value == null)
				return [];

			var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 0)
				return [];

			var ret = new HashSet<T>(parts.Length);
			foreach (var part in parts)
				ret.Add(parseInner(field, typeof(T), part));
			return ret;
		}

		static Dictionary<TKey, TValue> ParseDictionary<TKey, TValue>(string field, Type _, MiniYaml yaml,
			Func<string, Type, string, TKey> parseKey, Func<string, Type, string, TValue> parseValue)
		{
			if (yaml == null || yaml.Nodes.Length == 0)
				return [];

			var ret = new Dictionary<TKey, TValue>(yaml.Nodes.Length);
			foreach (var node in yaml.Nodes)
			{
				var key = parseKey(field, typeof(TKey), node.Key);
				var value = parseValue(field, typeof(TValue), node.Value.Value);
				ret.Add(key, value);
			}

			return ret;
		}

		static Dictionary<TKey, TValue> ParseDictionaryRecursive<TKey, TValue>(string field, Type _, MiniYaml yaml, Parsers parsers)
		{
			if (yaml == null || yaml.Nodes.Length == 0)
				return [];

			// Because Dictionaries can be nested, we require a signature for this method that can be recursively called.
			// So unlike the other method which resolves the parsers externally and pass that as a parseKey/parseValue argument,
			// we accept the Parsers bag instead.
			// If we tried to use parseKey/parseValue, we'd need generic types for the inner type.
			// But because we can recurse, those parsers might have inner types of their own, which would require another generic arg.
			// To avoid this need for infinite generic types from nesting, we accept the Parsers bag instead which requires no types,
			// at the small added cost of resolving the inner parser within the method.
			var parseKey = (Func<string, Type, MiniYaml, Parsers, TKey>)parsers.GetYamlParser(typeof(TKey));
			var parseValue = (Func<string, Type, MiniYaml, Parsers, TValue>)parsers.GetYamlParser(typeof(TValue));
			var ret = new Dictionary<TKey, TValue>(yaml.Nodes.Length);
			foreach (var node in yaml.Nodes)
			{
				var key = parseKey(field, typeof(TKey), new MiniYaml(node.Key), parsers);
				var value = parseValue(field, typeof(TValue), node.Value, parsers);
				ret.Add(key, value);
			}

			return ret;
		}

		static ImmutableArray<T> ParseImmutableArray<T>(string field, Type _, string value, Func<string, Type, string, T> parseInner)
		{
			if (value == null)
				return [];

			var type = typeof(T);
			T[] array;

			if (type == typeof(WVec))
				array = (T[])(object)ParseWVecArray(field, type, value);
			else if (type == typeof(CPos))
				array = (T[])(object)ParseCPosArray(field, type, value);
			else if (type == typeof(CVec))
				array = (T[])(object)ParseCVecArray(field, type, value);
			else if (type == typeof(int2))
				array = (T[])(object)ParseInt2Array(field, type, value);
			else
				array = ParseArray(field, type, value, parseInner);

			return array.ToImmutableArray();
		}

		static FrozenSet<T> ParseFrozenSet<T>(string field, Type _, string value, Func<string, Type, string, T> parseInner)
		{
			if (value == null)
				return FrozenSet<T>.Empty;

			return ParseHashSet(field, _, value, parseInner).ToFrozenSet();
		}

		static FrozenDictionary<TKey, TValue> ParseFrozenDictionary<TKey, TValue>(string field, Type _, MiniYaml yaml,
			Func<string, Type, string, TKey> parseKey, Func<string, Type, string, TValue> parseValue)
		{
			if (yaml == null)
				return FrozenDictionary<TKey, TValue>.Empty;

			return ParseDictionary(field, _, yaml, parseKey, parseValue).ToFrozenDictionary();
		}

		static FrozenDictionary<TKey, TValue> ParseFrozenDictionaryRecursive<TKey, TValue>(string field, Type _, MiniYaml yaml, Parsers parsers)
		{
			if (yaml == null)
				return FrozenDictionary<TKey, TValue>.Empty;

			return ParseDictionaryRecursive<TKey, TValue>(field, _, yaml, parsers).ToFrozenDictionary();
		}

		static BitSet<T> ParseBitSet<T>(string _1, Type _2, string value) where T : class
		{
			if (value == null)
				return default;

			var parts = value.Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			return new BitSet<T>(parts);
		}

		static T? ParseNullable<T>(string field, Type _, string value, Func<string, Type, string, T> parseInner) where T : struct
		{
			if (string.IsNullOrWhiteSpace(value))
				return null;

			return parseInner(field, typeof(T), value);
		}

		sealed class ParseViaTypeConverter
		{
			readonly TypeConverter typeConverter;

			public ParseViaTypeConverter(TypeConverter typeConverter)
			{
				this.typeConverter = typeConverter;
			}

			public T Parse<T>(string field, Type _, string value)
			{
				try
				{
					return (T)typeConverter.ConvertFromInvariantString(value);
				}
				catch
				{
					return (T)InvalidValueAction(value, typeof(T), field);
				}
			}
		}

		public static void Load<T>(T self, MiniYaml my)
		{
			var type = self.GetType();
			var loadClassDelegate = ClassLoadDelegates[type];
			if (loadClassDelegate == null)
				return;

			var yamlDict = my.ToDictionary();
			var missing = new List<string>();

			if (typeof(T) == type)
			{
				var loadClass = (Action<T, MiniYaml, Dictionary<string, MiniYaml>, List<string>>)loadClassDelegate;
				loadClass(self, my, yamlDict, missing);
			}
			else
			{
				loadClassDelegate.DynamicInvoke(self, my, yamlDict, missing);
			}

			if (missing.Count > 0)
				throw new MissingFieldsException(missing.ToArray());
		}

		public static T Load<T>(MiniYaml y) where T : new()
		{
			var t = new T();
			Load(t, y);
			return t;
		}

		public static void LoadFieldOrProperty(object target, string key, string value)
		{
			const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

			key = key.Trim();

			var field = target.GetType().GetField(key, Flags);
			if (field != null)
			{
				var fieldValue = typeof(FieldLoader)
					.GetMethod(nameof(GetValue))
					.MakeGenericMethod(field.FieldType)
					.Invoke(null, [field.Name, value]);

				field.SetValue(target, fieldValue);
				return;
			}

			var prop = target.GetType().GetProperty(key, Flags);
			if (prop != null)
			{
				var propValue = typeof(FieldLoader)
					.GetMethod(nameof(GetValue))
					.MakeGenericMethod(prop.PropertyType)
					.Invoke(null, [prop.Name, value]);

				prop.SetValue(target, propValue);
				return;
			}

			UnknownFieldAction(key, target.GetType());
		}

		public static T GetValue<T>(string field, string value)
		{
			var (parseDelegate, kind, parsers) = ParseDelegateCache[typeof(T)];
			if (parseDelegate != null)
			{
				switch (kind)
				{
					case ParseDelegateKind.MiniYamlValue:
					{
						var parseValueDelegate = (Func<string, Type, string, T>)parseDelegate;
						return parseValueDelegate(field, typeof(T), value);
					}

					case ParseDelegateKind.MiniYamlValueWithInnerParser:
					{
						return (T)parseDelegate.DynamicInvoke(field, typeof(T), value, parsers.InnerParser);
					}

					case ParseDelegateKind.MiniYamlNodesWithInnerParsers:
					{
						return (T)parseDelegate.DynamicInvoke(
							field, typeof(T), new MiniYaml(null), parsers.InnerKeyParser, parsers.InnerValueParser);
					}

					case ParseDelegateKind.MiniYamlNodesWithRecursiveParser:
					{
						var parseNodesDelegate = (Func<string, Type, MiniYaml, Parsers, T>)parseDelegate;
						return parseNodesDelegate(field, typeof(T), new MiniYaml(null), parsers);
					}
				}
			}

			UnknownFieldAction(field, typeof(T));
			return default;
		}

		public sealed class FieldLoadInfo
		{
			public readonly FieldInfo Field;
			public readonly SerializeAttribute Attribute;
			public readonly Func<MiniYaml, object> Loader;
			public string YamlName => Field.Name;

			public FieldLoadInfo(FieldInfo field, SerializeAttribute attr, Func<MiniYaml, object> loader = null)
			{
				Field = field;
				Attribute = attr;
				Loader = loader;
			}
		}

		public static IEnumerable<FieldLoadInfo> GetTypeLoadInfo(Type type)
		{
			return TypeLoadInfo[type].Where(fli => fli.Field.IsPublic || (fli.Attribute.Serialize && !fli.Attribute.IsDefault));
		}

		static FieldLoadInfo[] BuildTypeLoadInfo(Type type)
		{
			var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			var ret = new List<FieldLoadInfo>(fields.Length);

			foreach (var field in fields)
			{
				var attrs = field.GetCustomAttributes<SerializeAttribute>(false);
				if (attrs.Length > 1)
					throw new InvalidOperationException($"Multiple FieldLoader attributes on {type}.{field.Name}. At most one is supported.");

				var sa = attrs.Length == 1 ? attrs[0] : SerializeAttribute.Default;
				if (!sa.Serialize)
					continue;

				var loader = sa.GetLoader(type, field.FieldType);

				var fli = new FieldLoadInfo(field, sa, loader);
				ret.Add(fli);
			}

			return ret.ToArray();
		}

		class Parsers
		{
			/// <summary>
			/// For <see cref="ParseDelegateKind.MiniYamlValueWithInnerParser"/>, contains the inner parser.
			/// The delegate signature could be any of the possible forms.
			/// </summary>
			public Delegate InnerParser { get; private set; }

			/// <summary>
			/// For <see cref="ParseDelegateKind.MiniYamlNodesWithInnerParsers"/>, contains the inner key parser.
			/// The delegate signature will be only the one form that accepts a <see cref="string"/> value with no nested parser.
			/// </summary>
			public Delegate InnerKeyParser { get; set; }

			/// <summary>
			/// For <see cref="ParseDelegateKind.MiniYamlNodesWithInnerParsers"/>, contains the inner key parser.
			/// The delegate signature will be only the one form that accepts a <see cref="string"/> value with no nested parser.
			/// </summary>
			public Delegate InnerValueParser { get; set; }

			readonly List<Delegate> innerParsers = [];
			readonly List<Delegate> yamlParsers = [];

			FrozenDictionary<Type, Delegate> innerParsersLookup;
			FrozenDictionary<Type, Delegate> yamlParsersLookup;

			/// <summary>
			/// For <see cref="ParseDelegateKind.MiniYamlNodesWithRecursiveParser"/> returns the inner parser that returns the given <paramref name="type"/>.
			/// The delegate signature will be only the one form that accepts a <see cref="string"/> value with no nested parser.
			/// </summary>
			public Delegate GetInnerParser(Type type)
			{
				return innerParsersLookup[type];
			}

			/// <summary>
			/// For <see cref="ParseDelegateKind.MiniYamlNodesWithRecursiveParser"/> returns the inner parser that returns the given <paramref name="type"/>.
			/// The delegate signature will be only the one form that accepts a <see cref="MiniYaml"/>.
			/// </summary>
			public Delegate GetYamlParser(Type type)
			{
				return yamlParsersLookup[type];
			}

			public void Freeze()
			{
				innerParsersLookup = innerParsers.DistinctBy(d => d.Method.ReturnType).ToFrozenDictionary(d => d.Method.ReturnType);
				yamlParsersLookup = yamlParsers.DistinctBy(d => d.Method.ReturnType).ToFrozenDictionary(d => d.Method.ReturnType);
			}

			public void AddInnerParser(Delegate parser)
			{
				innerParsers.Add(parser);
				InnerParser = parser;
			}

			public void AddInnerAsYamlParser(Delegate parser)
			{
				Delegate wrapped = null;

				var parameters = parser.Method.GetParameters();
				if (parameters.Length >= 3 && parameters[2].ParameterType == typeof(string))
				{
					if (parameters.Length == 3)
					{
						// Func<string, Type, String, T>
						wrapped = (Delegate)typeof(Parsers)
							.GetMethod(nameof(ValueAsYamlParser), BindingFlags.Static | BindingFlags.NonPublic)
							.MakeGenericMethod(parser.Method.ReturnType)
							.Invoke(null, [parser]);
					}
					else if (parameters.Length == 4)
					{
						// Func<string, Type, String, Func<string, Type, String, U>, T>
						var innerType = parameters[3].ParameterType.GenericTypeArguments[3];
						wrapped = (Delegate)typeof(Parsers)
							.GetMethod(nameof(ValueWithInnerAsYamlParser), BindingFlags.Static | BindingFlags.NonPublic)
							.MakeGenericMethod([parser.Method.ReturnType, innerType])
							.Invoke(null, [parser]);
					}
				}
				else if (parameters.Length == 4 && parameters[2].ParameterType == typeof(MiniYaml))
				{
					// Func<string, Type, MiniYaml, ParserStack, T>
					wrapped = parser;
				}

				if (wrapped == null)
					throw new ArgumentException("Unexpected delegate signature", nameof(parser));

				yamlParsers.Add(wrapped);
			}

			static Func<string, Type, MiniYaml, Parsers, T> ValueAsYamlParser<T>(Func<string, Type, string, T> input)
			{
				return (n, t, y, p) => input(n, t, y.Value);
			}

			static Func<string, Type, MiniYaml, Parsers, T> ValueWithInnerAsYamlParser<T, U>(Func<string, Type, string, Func<string, Type, string, U>, T> input)
			{
				return (n, t, y, p) => input(n, t, y.Value, (Func<string, Type, string, U>)p.GetInnerParser(typeof(U)));
			}
		}

		static readonly FrozenDictionary<Type, Delegate> ParseDelegates =
			new Delegate[]
			{
				ParseInt,
				ParseByte,
				ParseShort,
				ParseUShort,
				ParseFloat,
				ParseDecimal,
				ParseString,
				ParseColor,
				ParseHotkey,
				ParseHotkeyReference,
				ParseWDist,
				ParseWVec,
				ParseWVecArray,
				ParseWPos,
				ParseWAngle,
				ParseWRot,
				ParseCPos,
				ParseCPosArray,
				ParseCVec,
				ParseCVecArray,
				ParseBooleanExpression,
				ParseIntegerExpression,
				ParseBool,
				ParseInt2Array,
				ParseSize,
				ParseInt2,
				ParseFloat2,
				ParseFloat3,
				ParseRectangle,
				ParseDateTime,
			}
			.ToFrozenDictionary(d => d.Method.ReturnType);

		enum ParseDelegateKind
		{
			/// <summary>
			/// The <see cref="MiniYaml.Value"/> should be parsed.
			/// </summary>
			MiniYamlValue,

			/// <summary>
			/// The <see cref="MiniYaml.Value"/> should be parsed. An additional parameter for an inner parser is required.
			/// </summary>
			MiniYamlValueWithInnerParser,

			/// <summary>
			/// The <see cref="MiniYaml.Nodes"/> should be parsed. Additional parameters for key/value parsers is required.
			/// </summary>
			MiniYamlNodesWithInnerParsers,

			/// <summary>
			/// The <see cref="MiniYaml.Nodes"/> should be parsed. An additional parameter for a parsers bag is required.
			/// </summary>
			MiniYamlNodesWithRecursiveParser,
		}

		static (Delegate ParseDelegate, ParseDelegateKind Kind) GetParseDelegate(Type fieldType, Parsers parsers)
		{
			static Delegate ParseValue(string methodName, Type[] innerType, Type fieldType)
			{
				return typeof(FieldLoader)
					.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
					.MakeGenericMethod(innerType)
					.CreateDelegate(
						typeof(Func<,,,>)
							.MakeGenericType(typeof(string), typeof(Type), typeof(string), fieldType));
			}

			static Delegate ParseValueWithInnerParser(string methodName, Type[] innerType, Type fieldType, Parsers parsers, out ParseDelegateKind outerKind)
			{
				outerKind = ParseDelegateKind.MiniYamlValueWithInnerParser;

				var (innerDelegate, innerKind) = GetParseDelegate(innerType[0], parsers);
				if (innerKind != ParseDelegateKind.MiniYamlValue)
					throw new InvalidOperationException("FieldLoader: Refused to nest collections (Array/List/HashSet)");
				if (innerDelegate == null)
					return null;

				parsers.AddInnerParser(innerDelegate);
				return typeof(FieldLoader)
					.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
					.MakeGenericMethod(innerType)
					.CreateDelegate(
						typeof(Func<,,,,>)
							.MakeGenericType(typeof(string), typeof(Type), typeof(string), innerDelegate.GetType(), fieldType));
			}

			var kind = ParseDelegateKind.MiniYamlValue;
			if (ParseDelegates.TryGetValue(fieldType, out var parseDelegate))
			{ }
			else if (fieldType.IsSZArray)
				parseDelegate = ParseValueWithInnerParser(nameof(ParseArray), [fieldType.GetElementType()], fieldType, parsers, out kind);
			else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
				parseDelegate = ParseValueWithInnerParser(nameof(ParseList), fieldType.GenericTypeArguments, fieldType, parsers, out kind);
			else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(HashSet<>))
				parseDelegate = ParseValueWithInnerParser(nameof(ParseHashSet), fieldType.GenericTypeArguments, fieldType, parsers, out kind);
			else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(BitSet<>))
				parseDelegate = ParseValue(nameof(ParseBitSet), fieldType.GenericTypeArguments, fieldType);
			else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Nullable<>))
				parseDelegate = ParseValueWithInnerParser(nameof(ParseNullable), fieldType.GenericTypeArguments, fieldType, parsers, out kind);
			else if (fieldType.IsGenericType && (fieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
				fieldType.GetGenericTypeDefinition()
					.BaseTypes()
					.Select(bt => bt.IsGenericType ? bt.GetGenericTypeDefinition() : null)
					.Any(bt => bt == typeof(FrozenDictionary<,>))))
			{
				var (innerKeyDelegate, innerKeyKind) = GetParseDelegate(fieldType.GenericTypeArguments[0], parsers);
				var (innerValueDelegate, innerValueKind) = GetParseDelegate(fieldType.GenericTypeArguments[1], parsers);
				if (innerKeyDelegate != null && innerValueDelegate != null)
				{
					var isMutable = fieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>);
					if (innerKeyKind == ParseDelegateKind.MiniYamlValue && innerValueKind == ParseDelegateKind.MiniYamlValue)
					{
						kind = ParseDelegateKind.MiniYamlNodesWithInnerParsers;

						parsers.InnerKeyParser = innerKeyDelegate;
						parsers.InnerValueParser = innerValueDelegate;
						parseDelegate = typeof(FieldLoader)
							.GetMethod(isMutable ? nameof(ParseDictionary) : nameof(ParseFrozenDictionary), BindingFlags.Static | BindingFlags.NonPublic)
							.MakeGenericMethod(fieldType.GenericTypeArguments)
							.CreateDelegate(
								typeof(Func<,,,,,>)
									.MakeGenericType(typeof(string), typeof(Type), typeof(MiniYaml),
										innerKeyDelegate.GetType(), innerValueDelegate.GetType(), fieldType));
					}
					else
					{
						kind = ParseDelegateKind.MiniYamlNodesWithRecursiveParser;

						parsers.AddInnerAsYamlParser(innerKeyDelegate);
						parsers.AddInnerAsYamlParser(innerValueDelegate);
						parseDelegate = typeof(FieldLoader)
							.GetMethod(isMutable ? nameof(ParseDictionaryRecursive) : nameof(ParseFrozenDictionaryRecursive), BindingFlags.Static | BindingFlags.NonPublic)
							.MakeGenericMethod(fieldType.GenericTypeArguments)
							.CreateDelegate(
								typeof(Func<,,,,>)
									.MakeGenericType(typeof(string), typeof(Type), typeof(MiniYaml), typeof(Parsers), fieldType));
					}
				}
			}
			else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
				parseDelegate = ParseValueWithInnerParser(nameof(ParseImmutableArray), fieldType.GenericTypeArguments, fieldType, parsers, out kind);
			else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(FrozenSet<>))
				parseDelegate = ParseValueWithInnerParser(nameof(ParseFrozenSet), fieldType.GenericTypeArguments, fieldType, parsers, out kind);
			else if (fieldType.IsEnum)
				parseDelegate = ParseValue(nameof(ParseEnum), [fieldType], fieldType);
			else
			{
				var typeConverter = TypeDescriptor.GetConverter(fieldType);
				if (typeConverter.CanConvertFrom(typeof(string)))
				{
					var helper = new ParseViaTypeConverter(typeConverter);
					parseDelegate = typeof(ParseViaTypeConverter)
						.GetMethod(nameof(ParseViaTypeConverter.Parse))
						.MakeGenericMethod(fieldType)
						.CreateDelegate(
							typeof(Func<,,,>)
								.MakeGenericType(typeof(string), typeof(Type), typeof(string), fieldType),
							helper);
				}
			}

			return (parseDelegate, kind);
		}

		static (Delegate ParseDelegate, ParseDelegateKind Kind, Parsers Parsers) CacheParseDelegate(Type fieldType)
		{
			var parsers = new Parsers();
			var (parseDelegate, kind) = GetParseDelegate(fieldType, parsers);
			parsers.Freeze();
			return (parseDelegate, kind, parsers);
		}

		static bool ShouldGetValueFromLoader(
			string fieldName,
			bool required,
			Dictionary<string, MiniYaml> yamlDict,
			ref List<string> missing)
		{
			if (!required || yamlDict.ContainsKey(fieldName))
				return true;

			missing.Add(fieldName);

			return false;
		}

		static bool TryGetValueFromYamlValue<T>(
			string fieldName,
			bool required,
			Dictionary<string, MiniYaml> yamlDict,
			ref List<string> missing,
			out T value,
			Func<string, Type, string, T> parser)
		{
			if (yamlDict.TryGetValue(fieldName, out var yaml))
			{
				value = parser(fieldName, typeof(T), yaml.Value);
				return true;
			}

			if (required)
				missing.Add(fieldName);

			value = default;
			return false;
		}

		static bool TryGetValueFromYamlValueWithInnerParser<T, U>(
			string fieldName,
			bool required,
			Dictionary<string, MiniYaml> yamlDict,
			ref List<string> missing,
			out T value,
			Func<string, Type, string, Func<string, Type, string, U>, T> parser,
			Func<string, Type, string, U> parseInner)
		{
			if (yamlDict.TryGetValue(fieldName, out var yaml))
			{
				value = parser(fieldName, typeof(T), yaml.Value, parseInner);
				return true;
			}

			if (required)
				missing.Add(fieldName);

			value = default;
			return false;
		}

		static bool TryGetValueFromYamlNodesWithInnerParsers<T, TKey, TValue>(
			string fieldName,
			bool required,
			Dictionary<string, MiniYaml> yamlDict,
			ref List<string> missing,
			out T value,
			Func<string, Type, MiniYaml, Func<string, Type, string, TKey>, Func<string, Type, string, TValue>, T> parser,
			Func<string, Type, string, TKey> parseKey,
			Func<string, Type, string, TValue> parseValue)
		{
			if (yamlDict.TryGetValue(fieldName, out var yaml))
			{
				value = parser(fieldName, typeof(T), yaml, parseKey, parseValue);
				return true;
			}

			if (required)
				missing.Add(fieldName);

			value = default;
			return false;
		}

		static bool TryGetValueFromYamlNodesWithRecursiveParser<T>(
			string fieldName,
			bool required,
			Dictionary<string, MiniYaml> yamlDict,
			ref List<string> missing,
			out T value,
			Func<string, Type, MiniYaml, Parsers, T> parser,
			Parsers parsers)
		{
			if (yamlDict.TryGetValue(fieldName, out var yaml))
			{
				value = parser(fieldName, typeof(T), yaml, parsers);
				return true;
			}

			if (required)
				missing.Add(fieldName);

			value = default;
			return false;
		}

		static Delegate BuildClassLoadDelegate(Type type)
		{
			var fieldLoadInfos = BuildTypeLoadInfo(type);
			if (fieldLoadInfos.Length == 0)
				return null;

			var target = Expression.Parameter(type, "target");
			var yaml = Expression.Parameter(typeof(MiniYaml), "yaml");
			var yamlDict = Expression.Parameter(typeof(Dictionary<string, MiniYaml>), "yamlDict");
			var missing = Expression.Parameter(typeof(List<string>), "missing");
			var variableExpressions = new List<ParameterExpression>(fieldLoadInfos.Length);
			var fieldExpressions = new List<Expression>(fieldLoadInfos.Length);

			foreach (var fieldLoadInfo in fieldLoadInfos)
			{
				var field = fieldLoadInfo.Field;

				var value = Expression.Variable(field.FieldType, "value");
				Expression tryGetValueCall;
				var loaderMethod = fieldLoadInfo.Attribute.GetLoaderMethod(type, field.FieldType);

				if (loaderMethod != null)
				{
					var shouldGetValueResult = Expression.Variable(typeof(bool), "shouldGetValue");
					var shouldGetValue = typeof(FieldLoader)
						.GetMethod(nameof(ShouldGetValueFromLoader), BindingFlags.Static | BindingFlags.NonPublic);
					var shouldGetValueCall = Expression.Call(
						null,
						shouldGetValue,
						Expression.Constant(field.Name),
						Expression.Constant(fieldLoadInfo.Attribute.Required),
						yamlDict,
						missing);

					Expression getLoaderValue = Expression.Call(loaderMethod, yaml);
					if (!loaderMethod.ReturnType.IsAssignableTo(field.FieldType))
						getLoaderValue = Expression.Convert(getLoaderValue, field.FieldType);
					var getAndAssignValue = Expression.Assign(value, getLoaderValue);

					tryGetValueCall = Expression.Block(
						[shouldGetValueResult],
						Expression.Assign(shouldGetValueResult, shouldGetValueCall),
						Expression.IfThen(shouldGetValueResult, getAndAssignValue),
						shouldGetValueResult);
				}
				else
				{
					var (parseDelegate, kind, parsers) = ParseDelegateCache[field.FieldType];
					if (parseDelegate != null)
					{
						string tryGetValueMethodName;
						Type[] genericArgs;
						Expression[] args;

						switch (kind)
						{
							default:
								throw new InvalidEnumArgumentException();

							case ParseDelegateKind.MiniYamlValue:
							{
								tryGetValueMethodName = nameof(TryGetValueFromYamlValue);
								genericArgs = [field.FieldType];
								args = [
									Expression.Constant(field.Name),
									Expression.Constant(fieldLoadInfo.Attribute.Required),
									yamlDict,
									missing,
									value,
									Expression.Constant(parseDelegate),
								];
								break;
							}

							case ParseDelegateKind.MiniYamlValueWithInnerParser:
							{
								tryGetValueMethodName = nameof(TryGetValueFromYamlValueWithInnerParser);
								genericArgs = [field.FieldType, parsers.InnerParser.Method.ReturnType];
								args = [
									Expression.Constant(field.Name),
									Expression.Constant(fieldLoadInfo.Attribute.Required),
									yamlDict,
									missing,
									value,
									Expression.Constant(parseDelegate),
									Expression.Constant(parsers.InnerParser),
								];
								break;
							}

							case ParseDelegateKind.MiniYamlNodesWithInnerParsers:
							{
								tryGetValueMethodName = nameof(TryGetValueFromYamlNodesWithInnerParsers);
								genericArgs = [field.FieldType, parsers.InnerKeyParser.Method.ReturnType, parsers.InnerValueParser.Method.ReturnType];
								args = [
									Expression.Constant(field.Name),
									Expression.Constant(fieldLoadInfo.Attribute.Required),
									yamlDict,
									missing,
									value,
									Expression.Constant(parseDelegate),
									Expression.Constant(parsers.InnerKeyParser),
									Expression.Constant(parsers.InnerValueParser),
								];
								break;
							}

							case ParseDelegateKind.MiniYamlNodesWithRecursiveParser:
							{
								tryGetValueMethodName = nameof(TryGetValueFromYamlNodesWithRecursiveParser);
								genericArgs = [field.FieldType];
								args = [
									Expression.Constant(field.Name),
									Expression.Constant(fieldLoadInfo.Attribute.Required),
									yamlDict,
									missing,
									value,
									Expression.Constant(parseDelegate),
									Expression.Constant(parsers),
								];
								break;
							}
						}

						var tryGetValue = typeof(FieldLoader)
							.GetMethod(tryGetValueMethodName, BindingFlags.Static | BindingFlags.NonPublic)
							.MakeGenericMethod(genericArgs);
						tryGetValueCall = Expression.Call(null, tryGetValue, args);
					}
					else
					{
						tryGetValueCall = Expression.Constant(false);
					}
				}

				Expression assignValueToTargetField;
				if (field.IsInitOnly)
				{
					// readonly fields cannot be assigned, fallback to runtime reflection to bypass.
					var setValue = typeof(FieldInfo).GetMethods().Single(
						m => m.Name == nameof(FieldInfo.SetValue) && m.GetParameters().Length == 2);
					var boxedValue = Expression.Convert(value, typeof(object));
					assignValueToTargetField = Expression.Call(Expression.Constant(field), setValue, target, boxedValue);
				}
				else
				{
					assignValueToTargetField = Expression.Assign(Expression.Field(target, field), value);
				}

				var fieldExpression = Expression.IfThen(tryGetValueCall, assignValueToTargetField);

				variableExpressions.Add(value);
				fieldExpressions.Add(fieldExpression);
			}

			var allFieldExpressions = Expression.Block(variableExpressions, fieldExpressions);
			var lambda = Expression.Lambda(
				allFieldExpressions,
				$"{nameof(FieldLoader)}_LoadClass_{type.Name}",
				[target, yaml, yamlDict, missing]);
			return lambda.Compile();
		}

		[AttributeUsage(AttributeTargets.Field)]
		public sealed class IgnoreAttribute : SerializeAttribute
		{
			public IgnoreAttribute()
				: base(serialize: false) { }
		}

		[AttributeUsage(AttributeTargets.Field)]
		public sealed class RequireAttribute : SerializeAttribute
		{
			public RequireAttribute()
				: base(serialize: true, required: true) { }
		}

		[AttributeUsage(AttributeTargets.Field)]
		public sealed class LoadUsingAttribute : SerializeAttribute
		{
			public LoadUsingAttribute(string loader, bool required = false)
				: base(serialize: true, required, loader) { }
		}

		[AttributeUsage(AttributeTargets.Field)]
		public class SerializeAttribute : Attribute
		{
			public static readonly SerializeAttribute Default = new(true);

			public bool IsDefault => this == Default;

			public readonly bool Serialize;
			public readonly bool Required;
			public readonly string Loader;

			protected SerializeAttribute(bool serialize = true, bool required = false, string loader = null)
			{
				Serialize = serialize;
				Required = required;
				Loader = loader;
			}

			internal MethodInfo GetLoaderMethod(Type type, Type fieldType)
			{
				const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

				if (!string.IsNullOrEmpty(Loader))
				{
					var method = type.GetMethod(Loader, Flags);
					if (method == null)
						throw new InvalidOperationException($"{type.Name} does not specify a loader function '{Loader}'");

					var parameters = method.GetParameters();
					if (parameters.Length != 1 || parameters[0].ParameterType != typeof(MiniYaml))
						throw new InvalidOperationException($"{type.Name} loader function '{Loader}' must accept only a single {nameof(MiniYaml)} parameter");

					// Legacy support: Allow LoadUsing to return an object instead of a concrete type.
					if (!method.ReturnType.IsAssignableTo(fieldType) && method.ReturnType != typeof(object))
						throw new InvalidOperationException($"{type.Name} loader function '{Loader}' should return a {fieldType}");

					return method;
				}

				return null;
			}

			internal Func<MiniYaml, object> GetLoader(Type type, Type fieldType)
			{
				var method = GetLoaderMethod(type, fieldType);
				if (method == null)
					return null;

				if (!method.ReturnType.IsValueType)
					return (Func<MiniYaml, object>)Delegate.CreateDelegate(typeof(Func<MiniYaml, object>), method);

				var del = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(MiniYaml), method.ReturnType), method);
				return yaml => del.DynamicInvoke(yaml);
			}
		}
	}
}
