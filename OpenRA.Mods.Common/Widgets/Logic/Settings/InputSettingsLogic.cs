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
using System.Collections.Generic;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class InputSettingsLogic : ChromeLogic
	{
		[FluentReference]
		const string Classic = "options-control-scheme.classic";

		[FluentReference]
		const string Modern = "options-control-scheme.modern";

		[FluentReference]
		const string Disabled = "options-mouse-scroll-type.disabled";

		[FluentReference]
		const string Standard = "options-mouse-scroll-type.standard";

		[FluentReference]
		const string Inverted = "options-mouse-scroll-type.inverted";

		[FluentReference]
		const string Joystick = "options-mouse-scroll-type.joystick";

		[FluentReference]
		const string Alt = "options-zoom-modifier.alt";

		[FluentReference]
		const string Ctrl = "options-zoom-modifier.ctrl";

		[FluentReference]
		const string Meta = "options-zoom-modifier.meta";

		[FluentReference]
		const string Shift = "options-zoom-modifier.shift";

		[FluentReference]
		const string None = "options-zoom-modifier.none";

		public class InputSettingsLogicDynamicWidgets : DynamicWidgets
		{
			public override ISet<string> WindowWidgetIds { get; } = EmptySet;
			public override IReadOnlyDictionary<string, string> ParentWidgetIdForChildWidgetId { get; } = EmptyDictionary;
			public override IReadOnlyDictionary<string, IReadOnlyCollection<string>> ParentDropdownWidgetIdsFromPanelWidgetId { get; } =
				new Dictionary<string, IReadOnlyCollection<string>>
				{
					{ "LABEL_DROPDOWN_TEMPLATE", new[] { "MOUSE_CONTROL_DROPDOWN", "MOUSE_SCROLL_TYPE_DROPDOWN", "ZOOM_MODIFIER" } },
				};
		}

		readonly InputSettingsLogicDynamicWidgets dynamicWidgets = new();
		readonly string classic;
		readonly string modern;

		[ObjectCreator.UseCtor]
		public InputSettingsLogic(Action<string, string, Func<Widget, Func<bool>>, Func<Widget, Action>> registerPanel, string panelID, string label)
		{
			classic = FluentProvider.GetString(Classic);
			modern = FluentProvider.GetString(Modern);

			registerPanel(panelID, label, InitPanel, ResetPanel);
		}

		Func<bool> InitPanel(Widget panel)
		{
			var gs = Game.Settings.Game;
			var scrollPanel = panel.Get<ScrollPanelWidget>("SETTINGS_SCROLLPANEL");

			SettingsUtils.BindCheckboxPref(panel, "ALTERNATE_SCROLL_CHECKBOX", gs, "UseAlternateScrollButton");
			SettingsUtils.BindCheckboxPref(panel, "EDGESCROLL_CHECKBOX", gs, "ViewportEdgeScroll");
			SettingsUtils.BindCheckboxPref(panel, "LOCKMOUSE_CHECKBOX", gs, "LockMouseWindow");
			SettingsUtils.BindSliderPref(panel, "ZOOMSPEED_SLIDER", gs, "ZoomSpeed");
			SettingsUtils.BindSliderPref(panel, "SCROLLSPEED_SLIDER", gs, "ViewportEdgeScrollStep");
			SettingsUtils.BindSliderPref(panel, "UI_SCROLLSPEED_SLIDER", gs, "UIScrollSpeed");

			var mouseControlDropdown = panel.Get<DropDownButtonWidget>("MOUSE_CONTROL_DROPDOWN");
			mouseControlDropdown.OnMouseDown = _ => ShowMouseControlDropdown(dynamicWidgets, mouseControlDropdown, gs);
			mouseControlDropdown.GetText = () => gs.UseClassicMouseStyle ? classic : modern;

			var mouseScrollDropdown = panel.Get<DropDownButtonWidget>("MOUSE_SCROLL_TYPE_DROPDOWN");
			mouseScrollDropdown.OnMouseDown = _ => ShowMouseScrollDropdown(dynamicWidgets, mouseScrollDropdown, gs);
			mouseScrollDropdown.GetText = () => gs.MouseScroll.ToString();

			var mouseControlDescClassic = panel.Get("MOUSE_CONTROL_DESC_CLASSIC");
			mouseControlDescClassic.IsVisible = () => gs.UseClassicMouseStyle;

			var mouseControlDescModern = panel.Get("MOUSE_CONTROL_DESC_MODERN");
			mouseControlDescModern.IsVisible = () => !gs.UseClassicMouseStyle;

			foreach (var container in new[] { mouseControlDescClassic, mouseControlDescModern })
			{
				var classicScrollRight = container.Get("DESC_SCROLL_RIGHT");
				classicScrollRight.IsVisible = () => gs.UseClassicMouseStyle ^ gs.UseAlternateScrollButton;

				var classicScrollMiddle = container.Get("DESC_SCROLL_MIDDLE");
				classicScrollMiddle.IsVisible = () => !gs.UseClassicMouseStyle ^ gs.UseAlternateScrollButton;

				var zoomDesc = container.Get("DESC_ZOOM");
				zoomDesc.IsVisible = () => gs.ZoomModifier == Modifiers.None;

				var zoomDescModifier = container.Get<LabelWidget>("DESC_ZOOM_MODIFIER");
				zoomDescModifier.IsVisible = () => gs.ZoomModifier != Modifiers.None;

				var zoomDescModifierTemplate = zoomDescModifier.GetText();
				var zoomDescModifierLabel = new CachedTransform<Modifiers, string>(
					mod => zoomDescModifierTemplate.Replace("MODIFIER", mod.ToString()));
				zoomDescModifier.GetText = () => zoomDescModifierLabel.Update(gs.ZoomModifier);

				var edgescrollDesc = container.Get<LabelWidget>("DESC_EDGESCROLL");
				edgescrollDesc.IsVisible = () => gs.ViewportEdgeScroll;
			}

			// Apply mouse focus preferences immediately
			var lockMouseCheckbox = panel.Get<CheckboxWidget>("LOCKMOUSE_CHECKBOX");
			var oldOnClick = lockMouseCheckbox.OnClick;
			lockMouseCheckbox.OnClick = () =>
			{
				// Still perform the old behaviour for clicking the checkbox, before
				// applying the changes live.
				oldOnClick();

				MakeMouseFocusSettingsLive();
			};

			var zoomModifierDropdown = panel.Get<DropDownButtonWidget>("ZOOM_MODIFIER");
			zoomModifierDropdown.OnMouseDown = _ => ShowZoomModifierDropdown(dynamicWidgets, zoomModifierDropdown, gs);
			zoomModifierDropdown.GetText = () => gs.ZoomModifier.ToString();

			SettingsUtils.AdjustSettingsScrollPanelLayout(scrollPanel);

			return () => false;
		}

		Action ResetPanel(Widget panel)
		{
			var gs = Game.Settings.Game;
			var dgs = new GameSettings();

			return () =>
			{
				gs.UseClassicMouseStyle = dgs.UseClassicMouseStyle;
				gs.MouseScroll = dgs.MouseScroll;
				gs.UseAlternateScrollButton = dgs.UseAlternateScrollButton;
				gs.LockMouseWindow = dgs.LockMouseWindow;
				gs.ViewportEdgeScroll = dgs.ViewportEdgeScroll;
				gs.ViewportEdgeScrollStep = dgs.ViewportEdgeScrollStep;
				gs.ZoomSpeed = dgs.ZoomSpeed;
				gs.UIScrollSpeed = dgs.UIScrollSpeed;
				gs.ZoomModifier = dgs.ZoomModifier;

				panel.Get<SliderWidget>("SCROLLSPEED_SLIDER").Value = gs.ViewportEdgeScrollStep;
				panel.Get<SliderWidget>("UI_SCROLLSPEED_SLIDER").Value = gs.UIScrollSpeed;

				MakeMouseFocusSettingsLive();
			};
		}

		public static void ShowMouseControlDropdown(
			DynamicWidgets dynamicWidgets,
			DropDownButtonWidget dropdown, GameSettings s)
		{
			var options = new Dictionary<string, bool>()
			{
				{ FluentProvider.GetString(Classic), true },
				{ FluentProvider.GetString(Modern), false },
			};

			ScrollItemWidget SetupItem(string o, ScrollItemWidget itemTemplate)
			{
				var item = ScrollItemWidget.Setup(itemTemplate,
					() => s.UseClassicMouseStyle == options[o],
					() => s.UseClassicMouseStyle = options[o]);
				item.Get<LabelWidget>("LABEL").GetText = () => o;
				return item;
			}

			dynamicWidgets.ShowDropDown(dropdown, "LABEL_DROPDOWN_TEMPLATE", 500, options.Keys, SetupItem);
		}

		static void ShowMouseScrollDropdown(
			InputSettingsLogicDynamicWidgets dynamicWidgets,
			DropDownButtonWidget dropdown, GameSettings s)
		{
			var options = new Dictionary<string, MouseScrollType>()
			{
				{ FluentProvider.GetString(Disabled), MouseScrollType.Disabled },
				{ FluentProvider.GetString(Standard), MouseScrollType.Standard },
				{ FluentProvider.GetString(Inverted), MouseScrollType.Inverted },
				{ FluentProvider.GetString(Joystick), MouseScrollType.Joystick },
			};

			ScrollItemWidget SetupItem(string o, ScrollItemWidget itemTemplate)
			{
				var item = ScrollItemWidget.Setup(itemTemplate,
					() => s.MouseScroll == options[o],
					() => s.MouseScroll = options[o]);
				item.Get<LabelWidget>("LABEL").GetText = () => o;
				return item;
			}

			dynamicWidgets.ShowDropDown(dropdown, "LABEL_DROPDOWN_TEMPLATE", 500, options.Keys, SetupItem);
		}

		static void ShowZoomModifierDropdown(
			InputSettingsLogicDynamicWidgets dynamicWidgets,
			DropDownButtonWidget dropdown, GameSettings s)
		{
			var options = new Dictionary<string, Modifiers>()
			{
				{ FluentProvider.GetString(Alt), Modifiers.Alt },
				{ FluentProvider.GetString(Ctrl), Modifiers.Ctrl },
				{ FluentProvider.GetString(Meta), Modifiers.Meta },
				{ FluentProvider.GetString(Shift), Modifiers.Shift },
				{ FluentProvider.GetString(None), Modifiers.None }
			};

			ScrollItemWidget SetupItem(string o, ScrollItemWidget itemTemplate)
			{
				var item = ScrollItemWidget.Setup(itemTemplate,
					() => s.ZoomModifier == options[o],
					() => s.ZoomModifier = options[o]);
				item.Get<LabelWidget>("LABEL").GetText = () => o;
				return item;
			}

			dynamicWidgets.ShowDropDown(dropdown, "LABEL_DROPDOWN_TEMPLATE", 500, options.Keys, SetupItem);
		}

		static void MakeMouseFocusSettingsLive()
		{
			var gameSettings = Game.Settings.Game;

			if (gameSettings.LockMouseWindow)
				Game.Renderer.GrabWindowMouseFocus();
			else
				Game.Renderer.ReleaseWindowMouseFocus();
		}
	}
}
