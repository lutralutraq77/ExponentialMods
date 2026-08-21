using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;
using Path = System.IO.Path;

namespace ExponentialMods;

/// <summary>
/// Exponential Mods -- one rule, applied to every item: each pickup moves the stack to the
/// next rung of a base^n ladder, bounded by a maximum exponent and a maximum stack that
/// respects the 32-bit integer Risk of Rain 2 stores item counts in.
/// </summary>
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
[BepInDependency("com.bepis.r2api", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("com.bepis.r2api.language", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("com.bepis.r2api.content_management", BepInDependency.DependencyFlags.HardDependency)]
public sealed class ExponentialModsPlugin : BaseUnityPlugin
{
	public const string PluginGUID = "com.lutralutra.exponentialmods";

	public const string PluginName = "Exponential Mods";

	public const string PluginVersion = "1.1.2";

	private ExponentialModsConfig _config;

	/// <summary>
	/// Suppresses our own re-entry. Both hooks call back into GiveItem, and without this
	/// the top-up grant would itself be scaled.
	/// </summary>
	private static int _hookDepth;

	private readonly Dictionary<string, ItemDef> _itemByDescToken = new Dictionary<string, ItemDef>(StringComparer.Ordinal);

	private readonly Dictionary<int, int> _localStacks = new Dictionary<int, int>();

	private string _cachedTooltip;

	private int _cachedTooltipRevision = -1;

	private bool _inLocalizationHook;

	private float _nextStackRefresh;

	private void Awake()
	{
		_config = new ExponentialModsConfig(Config, Logger);
		// Always register, so the artifact shows up in the lobby list even if the gate is off.
		ExponentialArtifact.Register(Path.GetDirectoryName(Info.Location) ?? string.Empty, Logger);

		// Hook GiveItemPermanent, NOT GiveItem. In current Risk of Rain 2 every public
		// grant overload -- GiveItem(ItemIndex,int), GiveItem(ItemDef,int),
		// GiveItemPermanent(ItemDef,int) -- funnels into GiveItemPermanent(ItemIndex,int),
		// and GiveItem itself is marked [Obsolete("Use .GiveItemPermanent instead.")] so the
		// game's own code no longer calls it. Hooking GiveItem only caught legacy callers,
		// which is why chest pickups under Artifact of Command scaled not at all.
		On.RoR2.Inventory.GiveItemPermanent_ItemIndex_int += Inventory_GiveItemPermanent;
		On.RoR2.Language.GetLocalizedStringByToken += Language_GetLocalizedStringByToken;
		On.RoR2.CharacterMaster.OnBodyStart += CharacterMaster_OnBodyStart;
		RoR2Application.onFixedUpdate += OnFixedUpdate;
		// _localStacks feeds the tooltip. Without this it keeps the last run's counts for
		// the rest of the process, so the main-menu logbook would advertise "4,096 -> 8,192"
		// for an item the player no longer owns.
		Run.onRunDestroyGlobal += OnRunDestroyed;
		RoR2Application.onLoad = (Action)Delegate.Combine(RoR2Application.onLoad, new Action(OnCatalogReady));

		Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Ladder: {_config.DescribeLadder()}");
		Logger.LogInfo(_config.RequireArtifact.Value
			? "Require Artifact is ON: scaling applies only while Artifact of Exponents is enabled for the run."
			: "Require Artifact is OFF: scaling is always active.");
		if (_config.UpgradedFromPreArtifactConfig && _config.RequireArtifact.Value)
		{
			Logger.LogWarning("Your config predates the artifact option, so \"Require Artifact\" has just defaulted to ON. " +
				"Scaling will NOT apply until you enable Artifact of Exponents when starting a run. " +
				"Set \"Require Artifact = false\" in the config to restore the previous always-on behaviour.");
		}
	}

	private void OnRunDestroyed(Run run)
	{
		_localStacks.Clear();
	}

	private void OnCatalogReady()
	{
		_config.RebuildBlockList();
		RebuildTooltipTokenCache();
		Logger.LogInfo($"Item catalog ready. Ladder: {_config.DescribeLadder()} (ceiling {_config.EffectiveCeiling:N0})");
	}

	// ---------------------------------------------------------------- eligibility

	/// <summary>
	/// The artifact gate. When "Require Artifact" is on, scaling only happens while
	/// Artifact of Exponents is enabled for the run -- an in-game on/off switch.
	/// If the artifact failed to register we fall back to always-on rather than
	/// silently disabling the whole mod.
	/// </summary>
	private bool IsLadderActive()
	{
		if (_config == null || !_config.Enabled.Value)
		{
			return false;
		}
		if (!_config.RequireArtifact.Value)
		{
			return true;
		}
		ArtifactGateState state = ExponentialArtifact.GetState();
		// Fail OPEN when there is no usable artifact. A stacking mod that silently does
		// nothing is far worse than one that scales when it arguably should not have.
		if (state == ArtifactGateState.Unavailable)
		{
			return true;
		}
		// NoRun gates identically to Off: no run means no grants to scale anyway.
		return state == ArtifactGateState.On;
	}

	/// <summary>
	/// Whether to describe the ladder on an item's tooltip. Deliberately NOT gated on the
	/// artifact: the lobby, character select and logbook are exactly where a player checks
	/// whether the mod is installed and whether to tick the artifact, and hiding the line
	/// there makes a working mod look broken.
	/// </summary>
	private bool ShouldDescribe(ItemDef def)
	{
		if (_config == null || !_config.Enabled.Value || (Object)(object)def == (Object)null)
		{
			return false;
		}
		return _config.IsTierAllowed(def) && !_config.IsBlocked(def);
	}

	private bool ShouldScale(Inventory inventory, ItemIndex itemIndex, out ItemDef itemDef)
	{
		itemDef = null;
		if (!IsLadderActive())
		{
			return false;
		}
		if ((Object)(object)inventory == (Object)null || itemIndex == ItemIndex.None)
		{
			return false;
		}
		ItemDef def = ItemCatalog.GetItemDef(itemIndex);
		if ((Object)(object)def == (Object)null)
		{
			return false;
		}
		if (!_config.IsTierAllowed(def) || _config.IsBlocked(def))
		{
			return false;
		}
		itemDef = def;
		return true;
	}

	private bool ShouldScale(ItemDef def)
	{
		if (!IsLadderActive() || (Object)(object)def == (Object)null)
		{
			return false;
		}
		return _config.IsTierAllowed(def) && !_config.IsBlocked(def);
	}

	// ---------------------------------------------------------------- hooks

	/// <summary>
	/// The single chokepoint for permanent item grants. Rewriting <paramref name="count"/>
	/// here covers every source -- ground pickups, Artifact of Command selections, printers,
	/// scrappers, cauldrons and other mods -- without needing to measure the inventory
	/// before and after, and without competing with the five other mods in this profile
	/// that hook GenericPickupController.AttemptGrant.
	/// </summary>
	private void Inventory_GiveItemPermanent(On.RoR2.Inventory.orig_GiveItemPermanent_ItemIndex_int orig, Inventory self, ItemIndex itemIndex, int count)
	{
		try
		{
			// Only single-stack grants are accelerated: bulk grants (command essence,
			// scrappers, inventory copies) must pass through untouched.
			if (_hookDepth > 0 || count != 1 || !NetworkServer.active || !ShouldScale(self, itemIndex, out var itemDef))
			{
				orig.Invoke(self, itemIndex, count);
				return;
			}
			int current = self.GetItemCountPermanent(itemIndex);
			int target = _config.GetNextRung(current);
			int gain = Math.Max(1, target - current);
			if (_config.DebugLogging.Value)
			{
				Logger.LogInfo($"{((Object)itemDef).name}: {current} -> {current + gain} (+{gain})");
			}
			_hookDepth++;
			try
			{
				orig.Invoke(self, itemIndex, gain);
			}
			finally
			{
				if (_hookDepth > 0)
				{
					_hookDepth--;
				}
			}
			if (IsLocalPlayerInventory(self))
			{
				_localStacks[(int)itemIndex] = self.GetItemCountPermanent(itemIndex);
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			orig.Invoke(self, itemIndex, count);
		}
	}

	private static bool IsLocalPlayerInventory(Inventory inventory)
	{
		if ((Object)(object)inventory == (Object)null)
		{
			return false;
		}
		try
		{
			CharacterBody body = ((Component)inventory).GetComponent<CharacterBody>();
			return IsLocalPlayerBody(body);
		}
		catch
		{
			return false;
		}
	}

	private string Language_GetLocalizedStringByToken(On.RoR2.Language.orig_GetLocalizedStringByToken orig, Language self, string token)
	{
		// RoR2 falls back to fallbackLanguage.GetLocalizedStringByToken for any token the
		// current language lacks, and that inner call is hooked too -- without this guard
		// the suffix would be appended twice for every untranslated token.
		if (_inLocalizationHook)
		{
			return orig.Invoke(self, token);
		}
		_inLocalizationHook = true;
		string text;
		try
		{
			text = orig.Invoke(self, token);
		}
		finally
		{
			_inLocalizationHook = false;
		}

		if (_config == null || !_config.ShowTooltip.Value || !_config.Enabled.Value)
		{
			return text;
		}
		if (string.IsNullOrEmpty(token) || !token.EndsWith("_DESC", StringComparison.Ordinal))
		{
			return text;
		}
		ItemDef itemDef = FindItemDefForDescToken(token);
		if (!ShouldDescribe(itemDef))
		{
			return text;
		}
		return text + GetTooltipSuffix(itemDef);
	}

	private void CharacterMaster_OnBodyStart(On.RoR2.CharacterMaster.orig_OnBodyStart orig, CharacterMaster self, CharacterBody body)
	{
		orig.Invoke(self, body);
		try
		{
			if (IsLocalPlayerBody(body))
			{
				RefreshLocalStacks(body.inventory);
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private void OnFixedUpdate()
	{
		if (Time.time < _nextStackRefresh)
		{
			return;
		}
		_nextStackRefresh = Time.time + 1f;
		try
		{
			for (int i = 0; i < NetworkUser.readOnlyInstancesList.Count; i++)
			{
				NetworkUser user = NetworkUser.readOnlyInstancesList[i];
				if ((Object)(object)user == (Object)null || !((NetworkBehaviour)user).isLocalPlayer)
				{
					continue;
				}
				CharacterMaster master = user.master;
				CharacterBody body = ((Object)(object)master != (Object)null) ? master.GetBody() : null;
				if ((Object)(object)body != (Object)null)
				{
					RefreshLocalStacks(body.inventory);
				}
				break;
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	// ---------------------------------------------------------------- helpers

	private void RefreshLocalStacks(Inventory inventory)
	{
		if ((Object)(object)inventory == (Object)null)
		{
			return;
		}
		int itemCount = ItemCatalog.itemCount;
		for (int i = 0; i < itemCount; i++)
		{
			// Record zeroes too, so a scrapped or consumed item does not leave a stale count.
			_localStacks[i] = inventory.GetItemCountPermanent((ItemIndex)i);
		}
	}

	/// <summary>
	/// The tooltip line depends only on the ladder settings, not on the item, so one cached
	/// string serves every item. This hook fires for every localized string the UI touches.
	/// </summary>
	private string GetTooltipSuffix(ItemDef itemDef)
	{
		if (_cachedTooltipRevision != _config.Revision || _cachedTooltip == null)
		{
			_cachedTooltip = $"\n<color=#D6B4FF>Exponential Mods: stacks climb {_config.EffectiveBase}^n</color>" +
				$"\n<color=#AAAAAA>{_config.DescribeLadder()}</color>" +
				$"\n<color=#AAAAAA>Ceiling: {_config.EffectiveCeiling:N0} stacks</color>";
			_cachedTooltipRevision = _config.Revision;
		}
		int current = 0;
		if (_localStacks.TryGetValue((int)itemDef.itemIndex, out var stack))
		{
			current = stack;
		}
		int next = _config.GetNextRung(current);
		ArtifactGateState gate = ExponentialArtifact.GetState();
		bool active = !_config.RequireArtifact.Value || gate == ArtifactGateState.On || gate == ArtifactGateState.Unavailable;

		// Only state the jump as fact when it would actually happen. With the gate closed the
		// real next pickup is vanilla +1, so an unqualified "Next pickup: 16 -> 32" directly
		// contradicts the caveat printed under it — and is read first.
		string suffix = _cachedTooltip + (active
			? $"\n<color=#88D5FF>Next pickup: {current:N0} → {next:N0} (+{next - current:N0})</color>"
			: $"\n<color=#AAAAAA>With the artifact: {current:N0} → {next:N0} (+{next - current:N0})</color>");

		if (!active)
		{
			// "Requires" outside a run, "Inactive" inside one. Saying "enable it" in the
			// lobby to a player who has already ticked it is the same false alarm 1.1.1 set
			// out to remove, just by another route.
			suffix += gate == ArtifactGateState.NoRun
				? "\n<color=#FFB454>Requires Artifact of Exponents</color>"
				: "\n<color=#FFB454>Inactive: enable Artifact of Exponents</color>";
		}
		return suffix;
	}

	private void RebuildTooltipTokenCache()
	{
		_itemByDescToken.Clear();
		int itemCount = ItemCatalog.itemCount;
		for (int i = 0; i < itemCount; i++)
		{
			ItemDef def = ItemCatalog.GetItemDef((ItemIndex)i);
			if ((Object)(object)def != (Object)null && !string.IsNullOrEmpty(def.descriptionToken))
			{
				_itemByDescToken[def.descriptionToken] = def;
			}
		}
	}

	private ItemDef FindItemDefForDescToken(string token)
	{
		if (_itemByDescToken.Count == 0)
		{
			RebuildTooltipTokenCache();
		}
		return _itemByDescToken.TryGetValue(token, out var def) ? def : null;
	}

	private static bool IsLocalPlayerBody(CharacterBody body)
	{
		if ((Object)(object)body == (Object)null)
		{
			return false;
		}
		try
		{
			return ((NetworkBehaviour)body).isLocalPlayer;
		}
		catch
		{
			return false;
		}
	}
}
