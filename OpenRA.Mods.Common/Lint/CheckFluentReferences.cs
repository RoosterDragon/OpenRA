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
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;
using OpenRA.Graphics;
using OpenRA.Mods.Common.LoadScreens;
using OpenRA.Mods.Common.Scripting;
using OpenRA.Mods.Common.Scripting.Global;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Mods.Common.Widgets.Logic;
using OpenRA.Scripting;
using OpenRA.Support;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Lint
{
	sealed class CheckFluentReferences : ILintPass, ILintMapPass
	{
		const BindingFlags StaticBinding = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

		void ILintMapPass.Run(Action<string> emitError, Action<string> emitWarning, ModData modData, Map map)
		{
			if (map.FluentMessageDefinitions == null)
				return;

			var usedKeys = ExtractMapFluentKeys(modData, map, emitWarning);
			foreach (var context in usedKeys.EmptyKeyContexts)
				emitWarning($"Empty key in map ftl files required by {context}");

			var mapMessages = FieldLoader.GetValue<ImmutableArray<string>>("value", map.FluentMessageDefinitions.Value);
			var modMessages = modData.Manifest.FluentMessages;

			// For maps we don't warn on unused keys. They might be unused on *this* map,
			// but the mod or another map may use them and we don't have sight of that.
			CheckKeys(modMessages.Concat(mapMessages), map.Open, usedKeys, _ => false, emitError, emitWarning);

			var modFluentBundle = new FluentBundle(modData.Manifest.FluentCulture, modMessages, modData.DefaultFileSystem, _ => { });
			var mapFluentBundle = new FluentBundle(modData.Manifest.FluentCulture, mapMessages, map, error => emitError(error.Message));

			foreach (var group in usedKeys.KeysWithContext)
			{
				if (modFluentBundle.HasMessage(group.Key))
				{
					if (mapFluentBundle.HasMessage(group.Key))
						emitWarning($"Key `{group.Key}` in map ftl files already exists in mod translations and will not be used.");
				}
				else if (!mapFluentBundle.HasMessage(group.Key))
				{
					foreach (var context in group)
						emitWarning($"Missing key `{group.Key}` in map ftl files required by {context}");
				}
			}

			if (map.FluentMessageDefinitions.Nodes.Length > 0)
				emitWarning(
					$"Lint pass ({nameof(CheckFluentReferences)}) lacks the know-how to test inline map fluent messages " +
					"- previous warnings may be incorrect");
		}

		void ILintPass.Run(Action<string> emitError, Action<string> emitWarning, ModData modData)
		{
			Console.WriteLine("Testing Fluent references");
			var usedKeys = ExtractModFluentKeys(modData, emitError, emitWarning);
			foreach (var context in usedKeys.EmptyKeyContexts)
				emitWarning($"Empty key in mod translation files required by {context}");

			var modMessages = modData.Manifest.FluentMessages.ToArray();

			// With the fully populated keys, check keys and variables are not missing and not unused across all language files.
			var keyWithAttrs = CheckKeys(modMessages, modData.DefaultFileSystem.Open, usedKeys,
				file =>
					!modData.Manifest.AllowUnusedFluentMessagesInExternalPackages ||
					!modData.DefaultFileSystem.IsExternalFile(file),
				emitError, emitWarning);

			foreach (var group in usedKeys.KeysWithContext)
				if (!keyWithAttrs.Contains(group.Key))
					foreach (var context in group)
						emitWarning($"Missing key `{group.Key}` in mod ftl files required by {context}");
		}

		static void ExtractRulesetFluentKeys(ModData modData, Ruleset rules, Keys keys)
		{
			foreach (var actorInfo in rules.Actors)
				foreach (var ti in actorInfo.Value.TraitInfos<TraitInfo>())
					ExtractFluentKeys(modData, ti, $"Actor `{actorInfo.Key}` trait {ti.GetType().Name[..^4]}", keys);

			foreach (var w in rules.Weapons)
				foreach (var wh in w.Value.Warheads)
					ExtractFluentKeys(modData, wh, $"Weapon `{w.Key}` warhead {wh.GetType().Name[..^7]}", keys);
		}

		static Keys ExtractMapFluentKeys(ModData modData, Map map, Action<string> emitWarning)
		{
			var keys = new Keys();
			ExtractRulesetFluentKeys(modData, map.Rules, keys);

			var luaScriptInfo = map.Rules.Actors[SystemActors.World].TraitInfoOrDefault<LuaScriptInfo>();
			if (luaScriptInfo != null)
			{
				// Matches expressions such as:
				// UserInterface.GetFluentMessage("fluent-key")
				// UserInterface.GetFluentMessage("fluent-key\"with-escape")
				// UserInterface.GetFluentMessage("fluent-key", { ["attribute"] = foo })
				// UserInterface.GetFluentMessage("fluent-key", { ["attribute\"-with-escape"] = foo })
				// UserInterface.GetFluentMessage("fluent-key", { ["attribute1"] = foo, ["attribute2"] = bar })
				// UserInterface.GetFluentMessage("fluent-key", tableVariable)
				// Extracts groups for the 'key' and each 'attr'.
				// If the table isn't inline like in the last example, extracts it as 'variable'.
				const string UserInterfaceFluentMessagePattern =
					@"UserInterface\s*\.\s*GetFluentMessage\s*\(" + // UserInterface.GetFluentMessage(
					@"\s*""(?<key>(?:[^""\\]|\\.)+?)""\s*" + // "fluent-key"
					@"(,\s*({\s*\[\s*""(?<attr>(?:[^""\\]|\\.)*?)""\s*\]\s*=\s*.*?" + // { ["attribute1"] = foo
					@"(\s*,\s*\[\s*""(?<attr>(?:[^""\\]|\\.)*?)""\s*\]\s*=\s*.*?)*\s*}\s*)" + // , ["attribute2"] = bar }
					"|\\s*,\\s*(?<variable>.*?))?" + // tableVariable
					@"\)"; // )
				var fluentMessageRegex = new Regex(UserInterfaceFluentMessagePattern);

				// The script in mods/common/scripts/utils.lua defines some helpers which accept a fluent key
				// Matches expressions such as:
				// AddPrimaryObjective(Player, "fluent-key")
				// AddSecondaryObjective(Player, "fluent-key")
				// AddPrimaryObjective(Player, "fluent-key\"with-escape")
				// Extracts groups for the 'key'.
				const string AddObjectivePattern =
					@"(AddPrimaryObjective|AddSecondaryObjective)\s*\(" + // AddPrimaryObjective(
					@".*?\s*,\s*""(?<key>(?:[^""\\]|\\.)+?)""\s*" + // Player, "fluent-key"
					@"\)"; // )
				var objectiveRegex = new Regex(AddObjectivePattern);

				foreach (var script in luaScriptInfo.Scripts)
				{
					if (!map.TryOpen(script, out var scriptStream))
						continue;

					using (scriptStream)
					{
						var scriptText = scriptStream.ReadAllText();
						IEnumerable<Match> matches = fluentMessageRegex.Matches(scriptText);
						if (luaScriptInfo.Scripts.Contains("utils.lua"))
							matches = matches.Concat(objectiveRegex.Matches(scriptText));

						var references = matches.Select(m =>
						{
							var key = m.Groups["key"].Value.Replace(@"\""", @"""");
							var attrs = m.Groups["attr"].Captures.Select(c => c.Value.Replace(@"\""", @"""")).ToArray();
							var variable = m.Groups["variable"].Value;
							var line = scriptText.Take(m.Index).Count(x => x == '\n') + 1;
							return (Key: key, Attrs: attrs, Variable: variable, Line: line);
						}).ToArray();

						foreach (var (key, attrs, variable, line) in references)
						{
							var context = $"Script {script}:{line}";
							keys.Add(key, new FluentReferenceAttribute(attrs), context);

							if (variable != "")
							{
								var userInterface = typeof(UserInterfaceGlobal).GetCustomAttribute<ScriptGlobalAttribute>().Name;
								const string FluentMessage = nameof(UserInterfaceGlobal.GetFluentMessage);
								emitWarning(
									$"{context} calls {userInterface}.{FluentMessage} with key `{key}` and args passed as `{variable}`." +
									"Inline the args at the callsite for lint analysis.");
							}
						}
					}
				}
			}

			return keys;
		}

		static Keys ExtractModFluentKeys(ModData modData, Action<string> emitError, Action<string> emitWarning)
		{
			var keys = new Keys();

			// Extract hardcoded core engine references
			ExtractConstFluentKeys(modData, typeof(Game), keys);

			// Extract references from mod.yaml (metadata, server traits, IGlobalModData)
			ExtractFluentKeys(modData, modData.Manifest.Metadata, "mod.yaml", keys);
			foreach (var traitName in modData.Manifest.ServerTraits)
			{
				var traitType = modData.ObjectCreator.FindType(traitName);
				if (traitType != null)
					ExtractConstFluentKeys(modData, traitType, keys);
			}

			var getModule = modData.GetType().GetMethod(nameof(ModData.GetOrNull), []);
			var globalModData = modData.ObjectCreator.GetTypesImplementing<IGlobalModData>()
				.Select(t => getModule?.MakeGenericMethod(t).Invoke(modData, []))
				.Where(x => x != null);

			foreach (var module in globalModData)
				ExtractFluentKeys(modData, module, "mod.yaml", keys);

			// Load screen
			var loadScreenType = modData.ObjectCreator.FindType(modData.Manifest.LoadScreen.Value);
			if (loadScreenType != null)
				ExtractConstFluentKeys(modData, loadScreenType, keys);

			// Traits, Weapons
			ExtractRulesetFluentKeys(modData, modData.DefaultRules, keys);
			foreach (var hotkey in modData.Hotkeys.Definitions)
				ExtractFluentKeys(modData, hotkey, $"Hotkey {hotkey.GetType().Name}", keys);

			// TerrainInfo
			foreach (var terrainInfo in modData.DefaultTerrainInfo.Values)
				ExtractFluentKeys(modData, terrainInfo, $"Tileset {terrainInfo.Id}", keys);

			// Chrome
			var modMessages = modData.Manifest.FluentMessages.ToImmutableArray();
			var fluentBundle = new FluentBundle(modData.Manifest.FluentCulture, modMessages, modData.DefaultFileSystem, error => emitError(error.Message));
			ExtractChromeFluentKeys(modData, keys, emitWarning, fluentBundle);

			return keys;
		}

		static void ExtractFluentKeys(ModData modData, object o, string prefix, Keys keys)
		{
			var type = o.GetType();
			ExtractConstFluentKeys(modData, type, keys);
			foreach (var f in Utility.GetFields(type))
			{
				var reference = Utility.GetCustomAttributes<FluentReferenceAttribute>(f, true).SingleOrDefault();
				if (reference != null)
					foreach (var key in LintExts.GetFieldValues(o, f, reference.DictionaryReference))
						keys.Add(key, reference, $"{prefix}.{f.Name}");

				var lint = Utility.GetCustomAttributes<IncludeFluentReferencesAttribute>(f, true).SingleOrDefault();
				if (lint != null)
					ExtractChildFluentKeys(modData, lint.DictionaryReference, f.GetValue(o), $"{prefix}.{f.Name}", keys);
			}
		}

		static void ExtractConstFluentKeys(ModData modData, Type t, Keys keys)
		{
			var classReferences = t.GetCustomAttributes<IncludeStaticFluentReferencesAttribute>(true);
			foreach (var classReference in classReferences)
				foreach (var referencedType in classReference.Types)
					ExtractConstFluentKeys(modData, referencedType, keys);

			foreach (var f in t.GetFields(StaticBinding))
			{
				var reference = Utility.GetCustomAttributes<FluentReferenceAttribute>(f, true).SingleOrDefault();
				if (reference != null)
					foreach (var key in LintExts.GetFieldValues(null, f, reference.DictionaryReference))
						keys.Add(key, reference, $"{t.Name}.{f.Name}");

				var lint = Utility.GetCustomAttributes<IncludeFluentReferencesAttribute>(f, true).SingleOrDefault();
				if (lint != null)
					ExtractChildFluentKeys(modData, lint.DictionaryReference, f.GetValue(null), $"{t.Name}.{f.Name}", keys);
			}
		}

		static void ExtractChildFluentKeys(ModData modData, LintDictionaryReference dictionaryReference,
			object fieldValue, string prefix, Keys keys)
		{
			var type = fieldValue.GetType();
			if (typeof(IEnumerable<object>).IsAssignableFrom(type))
				foreach (var o in (IEnumerable<object>)fieldValue)
					ExtractFluentKeys(modData, o, prefix, keys);

			Type dictionaryInterface = null;
			if (type.IsGenericType)
			{
				if (type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
					dictionaryInterface = type;
				else
					dictionaryInterface = type.GetInterface(typeof(IReadOnlyDictionary<,>).FullName);
			}

			if (dictionaryInterface != null)
			{
				// Use an intermediate list to cover the unlikely case where both keys and values are lintable.
				if (dictionaryReference.HasFlag(LintDictionaryReference.Keys))
				{
					IEnumerable fieldKeys = ((IDictionary)fieldValue).Keys;
					if (typeof(IEnumerable<object>).IsAssignableFrom(dictionaryInterface.GenericTypeArguments[0]))
						fieldKeys = ((ICollection<IEnumerable<object>>)fieldKeys).SelectMany(v => v);

					foreach (var k in fieldKeys)
						ExtractFluentKeys(modData, k, prefix, keys);
				}

				if (dictionaryReference.HasFlag(LintDictionaryReference.Values))
				{
					IEnumerable fieldValues = ((IDictionary)fieldValue).Values;
					if (typeof(IEnumerable<object>).IsAssignableFrom(dictionaryInterface.GenericTypeArguments[1]))
						fieldValues = ((ICollection<IEnumerable<object>>)fieldValues).SelectMany(v => v);

					foreach (var v in fieldValues)
						ExtractFluentKeys(modData, v, prefix, keys);
				}
			}
			else
				ExtractFluentKeys(modData, fieldValue, prefix, keys);
		}

		static void ExtractChromeFluentKeys(ModData modData, Keys usedKeys, Action<string> emitWarning, FluentBundle fluentBundle)
		{
			var (minEffectiveResolution, chromeLayoutNodes, rootsByNodeId) = BuildChromeTree(modData);

			var widgetTypes = modData.ObjectCreator.GetTypes()
				.Where(t => t.Name.EndsWith("Widget", StringComparison.InvariantCulture) && t.IsSubclassOf(typeof(Widget)))
				.ToList();

			var fluentReferencesByWidgetField = widgetTypes.SelectMany(t =>
				{
					var widgetName = t.Name[..^6];
					return Utility.GetFields(t)
						.Select(f =>
						{
							var attribute = Utility.GetCustomAttributes<FluentReferenceAttribute>(f, true).SingleOrDefault();
							return (WidgetName: widgetName, FieldName: f.Name, FluentReference: attribute);
						})
						.Where(x => x.FluentReference != null);
				})
				.ToDictionary(
					x => (x.WidgetName, x.FieldName),
					x => x.FluentReference);

			// Set up data we need to check the translation text fits on the widgets.
			var platform = Game.CreatePlatform("Default");
			var fontSheetBuilder = new SheetBuilder(SheetType.BGRA, 512);
			var fonts = modData.GetOrCreate<Fonts>().FontList.ToDictionary(x => x.Key,
				x => new SpriteFont(
					platform, x.Value.Font, modData.DefaultFileSystem.Open(x.Value.Font).ReadAllBytes(),
					x.Value.Size, x.Value.Ascender, 1f, fontSheetBuilder));
			ChromeMetrics.Initialize(modData);

			// Check that translations fit onto the widget.
			var uncheckedNodes = new List<MiniYamlNode>();
			foreach (var node in chromeLayoutNodes)
			{
				var nodeId = node.Key.Split('@')[1];
				if (rootsByNodeId.TryGetValue(nodeId, out var rootContext))
				{
					var allBounds = rootContext.Entries.Select(e => e.Bounds).ToArray();
					ExtractChromeFluentKeys(
						modData, node, fluentBundle, emitWarning, fluentReferencesByWidgetField, allBounds,
						usedKeys, minEffectiveResolution, fonts);
				}
				else
					uncheckedNodes.Add(node);
			}

			// For any nodes where we couldn't work out what their parent should be, we don't know the available size of the parent widget.
			// Instead, check them assuming they have the full window size available.
			foreach (var node in uncheckedNodes)
			{
				emitWarning($"Widget `{node.Key}` in {node.Location} does not have a known parent in the widget hierarchy, validation performed assuming window bounds.");
				var windowBounds = new WidgetBounds(0, 0, minEffectiveResolution.X, minEffectiveResolution.Y);
				ExtractChromeFluentKeys(
					modData, node, fluentBundle, emitWarning, fluentReferencesByWidgetField, [windowBounds],
					usedKeys, minEffectiveResolution, fonts);
			}
		}

		static void ExtractChromeFluentKeys(
			ModData modData,
			MiniYamlNode rootNode,
			FluentBundle fluentBundle,
			Action<string> emitWarning,
			Dictionary<(string WidgetName, string FieldName), FluentReferenceAttribute> fluentReferencesByWidgetField,
			IReadOnlyCollection<WidgetBounds> allParentBounds,
			Keys keys,
			int2 minEffectiveResolution,
			Dictionary<string, SpriteFont> fonts)
		{
			var allWidgetBounds = allParentBounds.Select(parentBounds => GetWidgetBounds(rootNode, parentBounds, minEffectiveResolution));

			// HACK: Some widgets that display icons don't bother with bounds, but instead use a icon size.
			// So we need to check if text fits on the icon, rather than within the bounds.
			var iconSize = rootNode.Value.NodeWithKeyOrDefault("IconSize")?.Value.Value;
			if (iconSize != null)
			{
				var iconSizeValues = iconSize.Split(",").Select(int.Parse).ToArray();
				allWidgetBounds = allWidgetBounds.Select(wb => new WidgetBounds(wb.X, wb.Y, iconSizeValues[0], iconSizeValues[1]));
			}

			var allWidgetBoundsArray = allWidgetBounds.ToArray();

			var nodeType = rootNode.Key.Split('@')[0];
			foreach (var childNode in rootNode.Value.Nodes)
			{
				var childType = childNode.Key.Split('@')[0];
				if (!fluentReferencesByWidgetField.TryGetValue((nodeType, childType), out var reference))
					continue;

				var key = childNode.Value.Value;
				keys.Add(key, reference, $"Widget `{rootNode.Key}` field `{childType}` in {rootNode.Location}");

				if (key == null)
					continue;

				// HACK: Tooltips don't display on the widget directly, don't validate their sizes.
				if (childType == "TooltipText" || childType == "TooltipDesc")
					continue;

				// HACK: Hardcode how each widget determines available fonts.
				var fontName = nodeType switch
				{
					"Button" or "DropDownButton" or "Checkbox" or "MenuButton" or "WorldButton" =>
						rootNode.Value.NodeWithKeyOrDefault("Font")?.Value.Value ?? ChromeMetrics.Get<string>("ButtonFont"),
					"Label" or "LabelWithHighlight" or "LabelForInput" =>
						rootNode.Value.NodeWithKeyOrDefault("Font")?.Value.Value ?? ChromeMetrics.Get<string>("TextFont"),
					"SupportPowers" =>
						rootNode.Value.NodeWithKeyOrDefault("OverlayFont")?.Value.Value ?? "TinyBold",
					"ProductionPalette" =>
						rootNode.Value.NodeWithKeyOrDefault("OverlayFont")?.Value.Value ?? "TinyBold",
					_ => null,
				};
				if (fontName == null)
				{
					emitWarning(
						$"`{key}` defined by `{rootNode.Key}` in field `{childType}` in {rootNode.Location} " +
						"is not a widget type whose font is recognised, validation performed using TextFont from ChromeMetrics.");
					fontName = ChromeMetrics.Get<string>("TextFont");
				}

				var font = fonts[fontName];
				var text = fluentBundle.GetMessage(key);
				foreach (var widgetBounds in allWidgetBoundsArray)
				{
					var widgetSize = new int2(widgetBounds.Width, widgetBounds.Height);

					// HACK: Apply the WordWrap that labels can apply.
					if ((nodeType == "Label" || nodeType == "LabelWithHighlight") &&
						bool.Parse(rootNode.Value.NodeWithKeyOrDefault("WordWrap")?.Value.Value ?? bool.FalseString))
						text = WidgetUtils.WrapText(text, widgetSize.X, font);

					var textSize = font.Measure(text);
					if (textSize.X > widgetSize.X || textSize.Y > widgetSize.Y)
						emitWarning(
							$"`{key}` defined by `{rootNode.Key}` in field `{childType}` in {rootNode.Location} " +
							$"has value `{text}`. Text is too large for widget. Text is {textSize}. Widget is {widgetSize}.");
				}
			}

			var widgetType = modData.ObjectCreator.FindType(nodeType + "Widget");
			ExtractConstFluentKeys(modData, widgetType, keys);

			Type[] logicArgsTypes = [typeof(Dictionary<string, MiniYaml>)];
			foreach (var childNode in rootNode.Value.Nodes)
			{
				if (childNode.Key == "Logic")
				{
					foreach (var logicName in FieldLoader.GetValue<ImmutableArray<string>>(childNode.Key, childNode.Value.Value))
					{
						var logicType = modData.ObjectCreator.FindType(logicName);
						if (logicType == null)
							continue;

						ExtractConstFluentKeys(modData, logicType, keys);

						var chromeArgsReferences = logicType.GetCustomAttributes<IncludeChromeLogicArgsFluentReferencesAttribute>(true);
						foreach (var methodName in chromeArgsReferences.SelectMany(a => a.MethodNames))
						{
							var dynamicReferencesMethod = logicType.GetMethod(methodName, StaticBinding, logicArgsTypes);
							var dynamicReferences = dynamicReferencesMethod.Invoke(null, [childNode.Value.ToDictionary()]);
							foreach (var (key, reference) in (IEnumerable<(string Key, FluentReferenceAttribute Reference)>)dynamicReferences)
								keys.Add(key, reference, logicType.Name);
						}
					}
				}

				if (childNode.Key == "Children")
					foreach (var n in childNode.Value.Nodes)
						ExtractChromeFluentKeys(
							modData, n, fluentBundle, emitWarning, fluentReferencesByWidgetField, allWidgetBoundsArray,
							keys, minEffectiveResolution, fonts);
			}
		}

		static WidgetBounds GetWidgetBounds(MiniYamlNode node, WidgetBounds parentBounds, int2 minEffectiveResolution)
		{
			// See Widget.Initialize & DropDownButtonWidget.ShowDropDown for reference.
			var substitutions = new Dictionary<string, int>
			{
				{ "WINDOW_WIDTH", minEffectiveResolution.X },
				{ "WINDOW_HEIGHT", minEffectiveResolution.Y },
				{ "PARENT_WIDTH", parentBounds.Right },
				{ "PARENT_HEIGHT", parentBounds.Bottom },
				{ "DROPDOWN_WIDTH", parentBounds.Width },
			};
			var xExpr = new IntegerExpression(node.Value.NodeWithKeyOrDefault("X")?.Value.Value ?? "0");
			var yExpr = new IntegerExpression(node.Value.NodeWithKeyOrDefault("Y")?.Value.Value ?? "0");
			var widthExpr = new IntegerExpression(node.Value.NodeWithKeyOrDefault("Width")?.Value.Value ?? "0");
			var heightExpr = new IntegerExpression(node.Value.NodeWithKeyOrDefault("Height")?.Value.Value ?? "0");
			var x = xExpr.Evaluate(substitutions);
			var y = yExpr.Evaluate(substitutions);
			var width = widthExpr.Evaluate(substitutions);
			var height = heightExpr.Evaluate(substitutions);
			return new WidgetBounds(x, y, width, height);
		}

		static (
			int2 MinEffectiveResolution,
			MiniYamlNode[] ChromeLayoutNodes,
			Dictionary<string, RootContext> RootsByNodeId) BuildChromeTree(ModData modData)
		{
			// MinEffectiveResolution is the minimum resolution we design the UI around.
			// This means we can check the translations fit for our minimum supported size.
			var minEffectiveResolution = new int2(modData.GetOrCreate<WorldViewportSizes>().MinEffectiveResolution);
			var windowBounds = new WidgetBounds(0, 0, minEffectiveResolution.X, minEffectiveResolution.Y);

			// Initial roots for possible widgets trees are given by LoadWidgetAtGameStartInfo.
			// Also handle windows created by ModContentLoadScreen.
			var rootsByNodeId = new Dictionary<string, RootContext>();
			var loadWidgetAtGameStartInfo = modData.DefaultRules.Actors[SystemActors.World].TraitInfo<LoadWidgetAtGameStartInfo>();
			rootsByNodeId[loadWidgetAtGameStartInfo.ShellmapRoot] = RootContext.CreateInitial(windowBounds);
			rootsByNodeId[loadWidgetAtGameStartInfo.IngameRoot] = RootContext.CreateInitial(windowBounds);
			rootsByNodeId[loadWidgetAtGameStartInfo.EditorRoot] = RootContext.CreateInitial(windowBounds);
			rootsByNodeId[loadWidgetAtGameStartInfo.GameSaveLoadingRoot] = RootContext.CreateInitial(windowBounds);
			rootsByNodeId[ModContentLogic.ContentPromptPanelWidgetId] = RootContext.CreateInitial(windowBounds);
			rootsByNodeId[ModContentLogic.ContentPanelWidgetId] = RootContext.CreateInitial(windowBounds);
			rootsByNodeId[ModContentLoadScreen.ModContentBackgroundWidgetId] = RootContext.CreateInitial(windowBounds);

			// Gather all the nodes together for evaluation.
			var chromeLayoutNodes = modData.Manifest.ChromeLayout
				.SelectMany(filename => MiniYaml.FromStream(modData.DefaultFileSystem.Open(filename), filename))
				.ToArray();

			// Stitch parent-> child widget relations together, until we have built the whole widget tree.
			// We loop multiple times, as each time we resolve a parent->child that allows
			// on the next pass for the children of those children to be resolved.
			// rootsByNodeId stores the state at the time the widget tree reached that location.
			// As child widgets might be parented to multiple places in the tree, multiple entrypoints are possible.
			// e.g. the same widget is used on two different screens. We track the bounds across all branches.
			var nodesLeftToBuild = chromeLayoutNodes.ToList();
			while (nodesLeftToBuild.Count > 0)
			{
				var builtNodes = new HashSet<MiniYamlNode>();
				foreach (var node in nodesLeftToBuild)
				{
					var nodeId = node.Key.Split('@')[1];
					if (rootsByNodeId.TryGetValue(nodeId, out var rootContext))
					{
						builtNodes.Add(node);

						// Snapshot Entries as it can be mutated.
						foreach (var entrypoint in rootContext.Entries.ToArray())
						{
							var outOfTreeParentChildWidgetIds = new Dictionary<string, HashSet<string>>();
							BuildChromeTreeBranch(
								modData, minEffectiveResolution, rootsByNodeId, outOfTreeParentChildWidgetIds,
								node, entrypoint.Bounds, new Stack<LogicCall>(entrypoint.Calls));
							BuildChromeTreeBranchForOutOfTree(
								minEffectiveResolution, rootsByNodeId, outOfTreeParentChildWidgetIds,
								node, entrypoint.Bounds, new Stack<LogicCall>(entrypoint.Calls));
						}
					}
				}

				if (builtNodes.Count == 0)
					break;

				nodesLeftToBuild.RemoveAll(builtNodes.Contains);
			}

			return (minEffectiveResolution, chromeLayoutNodes, rootsByNodeId);
		}

		static void WalkChromeTree(
			int2 minEffectiveResolution, MiniYamlNode node, WidgetBounds parentBounds, Stack<LogicCall> logicCallStack,
			Action<string, string, MiniYamlNode, WidgetBounds> nodeAction)
		{
			LogicCall logicCall = null;
			var logicNode = node.Value.NodeWithKeyOrDefault("Logic");
			if (logicNode != null)
			{
				var logics = logicNode.Value.Value.Split(",").Select(x => x.Trim()).ToArray();
				var logicArgs = logicNode.Value.ToDictionary();
				logicCallStack.Push(logicCall = new LogicCall(logics, logicArgs));
			}

			var bounds = GetWidgetBounds(node, parentBounds, minEffectiveResolution);

			var split = node.Key.Split('@');
			var nodeType = split[0];
			var nodeId = split.ElementAtOrDefault(1);
			nodeAction(nodeType, nodeId, node, bounds);

			foreach (var childNode in node.Value.Nodes)
				if (childNode.Key == "Children")
					foreach (var n in childNode.Value.Nodes)
						WalkChromeTree(minEffectiveResolution, n, bounds, logicCallStack, nodeAction);

			if (logicCall != null)
				logicCallStack.Pop();
		}

		static void BuildChromeTreeBranch(
			ModData modData, int2 minEffectiveResolution,
			Dictionary<string, RootContext> rootsByNodeId, Dictionary<string, HashSet<string>> outOfTreeParentChildWidgetIds,
			MiniYamlNode rootNode, WidgetBounds parentBounds, Stack<LogicCall> logicCallStack)
		{
			WalkChromeTree(minEffectiveResolution, rootNode, parentBounds, logicCallStack, (nodeType, nodeId, node, bounds) =>
			{
				if (nodeId == null)
					return;

				var windowBounds = new WidgetBounds(0, 0, minEffectiveResolution.X, minEffectiveResolution.Y);

				// Determine parent->child widget links that are created dynamically at runtime.
				// We can get a static reference of such relationships via derived classes of DynamicWidgets.
				var parentChildWidgetIds = GetParentChildWidgetIds(
					modData, logicCallStack, dw => dw.ParentWidgetIdForChildWidgetId, true);
				var dropdownParentChildWidgetIds = GetMultiParentChildWidgetIds(
					modData, logicCallStack, dw => dw.ParentDropdownWidgetIdsFromPanelWidgetId, true);
				var allParentChildWidgetIds = parentChildWidgetIds.Concat(dropdownParentChildWidgetIds)
					.GroupBy(x => x.Key)
					.ToDictionary(g => g.Key, g => g.SelectMany(kvp => kvp.Value).ToArray());

				// Determine out-of-tree links. This is where the logic grabs a widget outside the widget it has been given to manage.
				// e.g. it goes to Ui.Root and finds a widget from there.
				// This means the logic might be manging something outside its call stack.
				var localOutOfTreeParentChildWidgetIds = GetParentChildWidgetIds(
					modData, logicCallStack, dw => dw.OutOfTreeParentWidgetIdForChildWidgetId, false);
				foreach (var kvp in localOutOfTreeParentChildWidgetIds)
				{
					var parentWidgetId = kvp.Key.ParentWidgetId;
					if (parentWidgetId == "")
					{
						// A blank parent indicates the parent is Ui.Root. Add it with the window area.
						foreach (var childWidgetId in kvp.Value)
							rootsByNodeId.TryAdd(childWidgetId, RootContext.CreateInitial(windowBounds));
					}
					else
					{
						// Save this link for later, we'll walk the tree again and link up out-of-tree elements.
						var entries = outOfTreeParentChildWidgetIds.GetOrAdd(parentWidgetId, _ => []);
						entries.UnionWith(kvp.Value);
					}
				}

				// Add any windows the logic can open.
				var windowWidgetIds = GetLogicWidgets(modData, logicCallStack, true)
					.SelectMany(x => x.DynamicWidgets.WindowWidgetIds);
				foreach (var windowWidgetId in windowWidgetIds)
					rootsByNodeId.TryAdd(windowWidgetId, RootContext.CreateInitial(windowBounds));

				// If we've resolved the parent, set up the child bounds for the next pass.
				// For every logic that is if effect in this call stack we'll
				// add bounds for every child widget it links up dynamically.
				foreach (var logic in logicCallStack.SelectMany(c => c.Logics).Distinct())
					if (allParentChildWidgetIds.TryGetValue((logic, nodeId), out var childOfParentNodeIds))
						foreach (var childOfParentNodeId in childOfParentNodeIds)
							rootsByNodeId.GetOrAdd(childOfParentNodeId, _ => RootContext.CreateEmpty()).Add(bounds, logicCallStack);
			});

			static Dictionary<(string Logic, string ParentWidgetId), string[]> GetParentChildWidgetIds(
				ModData modData, Stack<LogicCall> logicCallStack,
				Func<ChromeLogic.DynamicWidgets, IReadOnlyDictionary<string, string>> parentWidgetIdForChildWidgetId,
				bool logicMustBeOnCallStack)
			{
				return GetLogicWidgets(modData, logicCallStack, logicMustBeOnCallStack)
					.SelectMany(x =>
						parentWidgetIdForChildWidgetId(x.DynamicWidgets)
							.GroupBy(kvp => kvp.Value)
							.Select(g => (x.Logic, ParentWidgetId: g.Key, ChildWidgetIds: g.Select(kvp => kvp.Key).ToArray())))
					.GroupBy(x => (x.Logic, x.ParentWidgetId))
					.ToDictionary(g => g.Key, g => g.SelectMany(x => x.ChildWidgetIds).ToArray());
			}

			static Dictionary<(string Logic, string ParentWidgetId), string[]> GetMultiParentChildWidgetIds(
				ModData modData, Stack<LogicCall> logicCallStack,
				Func<ChromeLogic.DynamicWidgets, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> parentWidgetIdsForChildWidgetId,
				bool logicMustBeOnCallStack)
			{
				return GetLogicWidgets(modData, logicCallStack, logicMustBeOnCallStack)
					.SelectMany(x =>
						parentWidgetIdsForChildWidgetId(x.DynamicWidgets)
							.SelectMany(kvp => kvp.Value.Select(v => (ChildWidgetId: kvp.Key, ParentWidgetId: v)))
							.GroupBy(x => x.ParentWidgetId)
							.Select(g => (x.Logic, ParentWidgetId: g.Key, ChildWidgetIds: g.Select(x => x.ChildWidgetId).ToArray())))
					.GroupBy(x => (x.Logic, x.ParentWidgetId))
					.ToDictionary(g => g.Key, g => g.SelectMany(x => x.ChildWidgetIds).ToArray());
			}

			static IEnumerable<(string Logic, ChromeLogic.DynamicWidgets DynamicWidgets)> GetLogicWidgets(
				ModData modData, Stack<LogicCall> logicCallStack, bool logicMustBeOnCallStack)
			{
				return modData.ObjectCreator.GetTypes()
					.Where(t =>
						t.IsSubclassOf(typeof(ChromeLogic.DynamicWidgets)) &&
						typeof(ChromeLogic).IsAssignableFrom(t.ReflectedType))
					.SelectMany(t =>
					{
						var reflectedTypeName = t.ReflectedType.Name;
						return logicCallStack
							.Where(c => !logicMustBeOnCallStack || c.Logics.Contains(reflectedTypeName))
							.Select(c =>
								modData.ObjectCreator.CreateObject<ChromeLogic.DynamicWidgets>(
									$"{reflectedTypeName}+{t.Name}",
									new Dictionary<string, object> { { "logicArgs", c.LogicArgs } }))
							.Select(dw => (Logic: reflectedTypeName, DynamicWidgets: dw));
					});
			}
		}

		static void BuildChromeTreeBranchForOutOfTree(
			int2 minEffectiveResolution,
			Dictionary<string, RootContext> rootsByNodeId, Dictionary<string, HashSet<string>> outOfTreeParentChildWidgetIds,
			MiniYamlNode rootNode, WidgetBounds parentBounds, Stack<LogicCall> logicCallStack)
		{
			WalkChromeTree(minEffectiveResolution, rootNode, parentBounds, logicCallStack, (nodeType, nodeId, node, bounds) =>
			{
				// Tooltips operate out-of-tree, as the widget tree has a single container widget for all tooltips.
				var tooltipContainer = node.Value.NodeWithKeyOrDefault("TooltipContainer");
				var tooltipTemplate = node.Value.NodeWithKeyOrDefault("TooltipTemplate");
				if (tooltipContainer != null || tooltipTemplate != null)
				{
					var container = tooltipContainer?.Value.Value;
					var template = tooltipTemplate?.Value.Value;

					// HACK: Hardcode the default values for nodes that have a default in code and don't force a value in YAML.
					container ??= "TOOLTIP_CONTAINER"; // Fallback, if a new type ever gets added that doesn't require this to be set in YAML.
					template ??= nodeType switch
					{
						"ClientTooltipRegion" =>
							node.Value.NodeWithKey("Template").Value.Value, // Breaks the usual convention of 'TooltipTemplate'.
						"Button" or "DropDownButton" or "Checkbox" or "MenuButton" or "WorldButton" or "ProductionTypeButton" or "ScrollItem" =>
							"BUTTON_TOOLTIP",
						"ObserverProductionIcons" or "ProductionPalette" =>
							"PRODUCTION_TOOLTIP",
						"ObserverSupportPowerIcons" or "SupportPowers" =>
							"SUPPORT_POWER_TOOLTIP",
						"ObserverArmyIcons" =>
							"ARMY_TOOLTIP",
						"MapPreview" =>
							"SPAWN_TOOLTIP",
						"ViewportController" =>
							"WORLD_TOOLTIP",
						_ => "SIMPLE_TOOLTIP", // Fallback, for any type we haven't got the correct hardcoded value for.
					};

					// Add discovered tooltips. Tooltips determine their own size so the bounds are irrelevant.
					// However adding them to the roots list allows us to mark them as widgets with known parents.
					foreach (var logic in logicCallStack.SelectMany(c => c.Logics).Distinct())
						rootsByNodeId.GetOrAdd(template, _ => RootContext.CreateEmpty()).Add(new WidgetBounds(0, 0, 0, 0), logicCallStack);
				}

				if (nodeId == null)
					return;

				// For out-of-tree widgets, assume the full window bounds is available to them.
				// As out-of-tree widgets might be managed by a logic outside their call stack,
				// we ignore the callstack when making checks here.
				var windowBounds = new WidgetBounds(0, 0, minEffectiveResolution.X, minEffectiveResolution.Y);
				if (outOfTreeParentChildWidgetIds.TryGetValue(nodeId, out var childOfParentNodeIds))
					foreach (var childOfParentNodeId in childOfParentNodeIds)
						rootsByNodeId.GetOrAdd(childOfParentNodeId, _ => RootContext.CreateEmpty()).Add(windowBounds, logicCallStack);
			});
		}

		static HashSet<string> CheckKeys(
			IEnumerable<string> paths, Func<string, Stream> openFile, Keys usedKeys,
			Func<string, bool> checkUnusedKeysForFile, Action<string> emitError, Action<string> emitWarning)
		{
			var keyWithAttrs = new HashSet<string>();
			foreach (var path in paths)
			{
				var stream = openFile(path);
				using (var reader = new StreamReader(stream))
				{
					var parser = new LinguiniParser(reader);
					var result = parser.Parse();

					foreach (var entry in result.Entries)
					{
						if (entry is not AstMessage message)
							continue;

						IEnumerable<(Pattern Node, string AttributeName)> nodeAndAttributeNames;
						if (message.Attributes.Count == 0)
							nodeAndAttributeNames = [(message.Value, null)];
						else
							nodeAndAttributeNames = message.Attributes.Select(a => (a.Value, a.Id.Name.ToString()));

						var key = message.GetId();
						foreach (var (node, attributeName) in nodeAndAttributeNames)
						{
							keyWithAttrs.Add(attributeName == null ? key : $"{key}.{attributeName}");
							if (checkUnusedKeysForFile(path))
								CheckUnusedKey(key, attributeName, path, usedKeys, emitWarning);
							CheckVariables(node, key, attributeName, path, usedKeys, emitError, emitWarning);
						}
					}
				}
			}

			return keyWithAttrs;

			static void CheckUnusedKey(string key, string attribute, string file, Keys usedKeys, Action<string> emitWarning)
			{
				var isAttribute = !string.IsNullOrEmpty(attribute);
				var keyWithAtrr = isAttribute ? $"{key}.{attribute}" : key;

				if (!usedKeys.Contains(keyWithAtrr))
					emitWarning(isAttribute ?
						$"Unused attribute `{attribute}` of key `{key}` in {file}" :
						$"Unused key `{key}` in {file}");
			}

			static void CheckVariables(
				Pattern node, string key, string attribute, string file, Keys usedKeys,
				Action<string> emitError, Action<string> emitWarning)
			{
				var isAttribute = !string.IsNullOrEmpty(attribute);
				var keyWithAtrr = isAttribute ? $"{key}.{attribute}" : key;

				if (!usedKeys.TryGetRequiredVariables(keyWithAtrr, out var requiredVariables))
					return;

				var variableNames = new HashSet<string>();
				foreach (var element in node.Elements)
				{
					if (element is not Placeable placeable)
						continue;

					AddVariableAndCheckUnusedVariable(placeable);
					if (placeable.Expression is SelectExpression selectExpression)
						foreach (var variant in selectExpression.Variants)
							foreach (var variantElement in variant.Value.Elements)
								if (variantElement is Placeable variantPlaceable)
									AddVariableAndCheckUnusedVariable(variantPlaceable);
				}

				void AddVariableAndCheckUnusedVariable(Placeable placeable)
				{
					if (placeable.Expression is not IInlineExpression inlineExpression ||
						inlineExpression is not VariableReference variableReference)
						return;

					var name = variableReference.Id.Name.ToString();
					variableNames.Add(name);

					if (!requiredVariables.Contains(name))
						emitWarning(isAttribute ?
							$"Unused variable `{name}` for attribute `{attribute}` of key `{key}` in {file}" :
							$"Unused variable `{name}` for key `{key}` in {file}");
				}

				foreach (var name in requiredVariables)
					if (!variableNames.Contains(name))
						emitError(isAttribute ?
							$"Missing variable `{name}` for attribute `{attribute}` of key `{key}` in {file}" :
							$"Missing variable `{name}` for key `{key}` in {file}");
			}
		}

		sealed class Keys
		{
			readonly HashSet<string> keys = [];
			readonly List<(string Key, string Context)> keysWithContext = [];
			readonly Dictionary<string, HashSet<string>> requiredVariablesByKey = [];
			readonly List<string> contextForEmptyKeys = [];

			public void Add(string key, FluentReferenceAttribute fluentReference, string context)
			{
				if (key == null)
				{
					if (!fluentReference.Optional)
						contextForEmptyKeys.Add(context);
					return;
				}

				if (fluentReference.RequiredVariableNames != null && fluentReference.RequiredVariableNames.Length > 0)
				{
					var rv = requiredVariablesByKey.GetOrAdd(key, _ => []);
					rv.UnionWith(fluentReference.RequiredVariableNames);
				}

				keys.Add(key);
				keysWithContext.Add((key, context));
			}

			public bool TryGetRequiredVariables(string key, out IReadOnlySet<string> requiredVariables)
			{
				if (requiredVariablesByKey.TryGetValue(key, out var rv))
				{
					requiredVariables = rv;
					return true;
				}

				requiredVariables = null;
				return false;
			}

			public bool Contains(string key)
			{
				return keys.Contains(key);
			}

			public ILookup<string, string> KeysWithContext => keysWithContext.OrderBy(x => x.Key).ToLookup(x => x.Key, x => x.Context);

			public IEnumerable<string> EmptyKeyContexts => contextForEmptyKeys;
		}

		sealed record class LogicCall(string[] Logics, Dictionary<string, MiniYaml> LogicArgs);

		sealed class RootContext
		{
			public sealed record class Entry(WidgetBounds Bounds, ImmutableArray<LogicCall> Calls);

			public List<Entry> Entries { get; }

			RootContext(List<Entry> entries) { Entries = entries; }

			public static RootContext CreateEmpty()
			{
				return new RootContext([]);
			}

			public static RootContext CreateInitial(WidgetBounds bounds)
			{
				return new RootContext([new(bounds, [])]);
			}

			public void Add(WidgetBounds bounds, IEnumerable<LogicCall> calls)
			{
				Entries.Add(new Entry(bounds, calls.ToImmutableArray()));
			}
		}
	}
}
