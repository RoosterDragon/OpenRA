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
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.LoadScreens
{
	public sealed class ModContentLoadScreen : SheetLoadScreen
	{
		public const string ContentPromptPanelWidgetId = "CONTENT_PROMPT_PANEL";
		public const string ContentPanelWidgetId = "CONTENT_PANEL";
		public const string ModContentBackgroundWidgetId = "MODCONTENT_BACKGROUND";

		Sprite sprite;
		Rectangle bounds;

		Sheet lastSheet;
		int lastDensity;
		Size lastResolution;

		public override void DisplayInner(Renderer r, Sheet s, int density)
		{
			if (s != lastSheet || density != lastDensity)
			{
				lastSheet = s;
				lastDensity = density;
				sprite = CreateSprite(s, density, new Rectangle(0, 0, 1024, 480));
			}

			if (r.Resolution != lastResolution)
			{
				lastResolution = r.Resolution;
				bounds = new Rectangle(0, 0, lastResolution.Width, lastResolution.Height);
			}

			WidgetUtils.FillRectWithSprite(bounds, sprite);
		}

		public override void StartGame(Arguments args)
		{
			var modId = args.GetValue("Content.Mod", null);
			if (modId == null || !Game.Mods.TryGetValue(modId, out var selectedMod))
				throw new InvalidOperationException("Invalid or missing Content.Mod argument.");

			var translationFilePath = args.GetValue("Content.TranslationFile", null);
			if (translationFilePath == null || !File.Exists(translationFilePath))
				throw new InvalidOperationException("Invalid or missing Content.TranslationFile argument.");

			var content = selectedMod.Get<ModContent>(Game.ModData.ObjectCreator);

			Ui.LoadWidgetUnchecked(ModContentBackgroundWidgetId, Ui.Root, new WidgetArgs());

			if (!IsModInstalled(content))
			{
				var widgetArgs = new WidgetArgs
				{
					{ "continueLoading", () => Game.RunAfterTick(() => Game.InitializeMod(modId, new Arguments())) },
					{ "mod", selectedMod },
					{ "content", content },
					{ "translationFilePath", translationFilePath },
				};

				Ui.OpenWindowUnchecked(ContentPromptPanelWidgetId, widgetArgs);
			}
			else
			{
				var widgetArgs = new WidgetArgs
				{
					{ "onCancel", () => Game.RunAfterTick(() => Game.InitializeMod(modId, new Arguments())) },
					{ "mod", selectedMod },
					{ "content", content },
					{ "translationFilePath", translationFilePath },
				};

				Ui.OpenWindowUnchecked(ContentPanelWidgetId, widgetArgs);
			}
		}

		static bool IsModInstalled(ModContent content)
		{
			return content.Packages
				.Where(p => p.Value.Required)
				.All(p => p.Value.TestFiles.All(f => File.Exists(Platform.ResolvePath(f))));
		}

		public override bool BeforeLoad()
		{
			return true;
		}
	}
}
