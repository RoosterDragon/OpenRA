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
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class LoadLocalPlayerProfileLogic : ChromeLogic
	{
		public class LoadLocalPlayerProfileLogicDynamicWidgets : DynamicWidgets
		{
			public override IReadOnlySet<string> WindowWidgetIds { get; } = EmptySet;
			public override IReadOnlyDictionary<string, string> ParentWidgetIdForChildWidgetId { get; } =
				new Dictionary<string, string>
				{
					{ "LOCAL_PROFILE_PANEL", "PLAYER_PROFILE_CONTAINER" },
				};
		}

		readonly LoadLocalPlayerProfileLogicDynamicWidgets dynamicWidgets = new();

		[ObjectCreator.UseCtor]
		public LoadLocalPlayerProfileLogic(Widget widget, World world)
		{
			Func<bool> minimalProfile = () => Ui.CurrentWindow() != null;

			dynamicWidgets.LoadWidget(widget, "LOCAL_PROFILE_PANEL", new WidgetArgs()
			{
				{ "minimalProfile", minimalProfile }
			});
		}
	}
}
