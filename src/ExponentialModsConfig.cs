using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using Object = UnityEngine.Object;

namespace ExponentialMods;

/// <summary>
/// Every knob this mod has. Deliberately small: one ladder, applied uniformly.
/// </summary>
internal sealed class ExponentialModsConfig
{
	private const string SectionLadder = "Ladder";

	private const string SectionLimits = "Limits";

	private const string SectionFilters = "Filters";

	private readonly ManualLogSource _log;

	private readonly HashSet<int> _blockedIndices = new HashSet<int>();

	private bool _catalogReady;

	public readonly ConfigEntry<bool> Enabled;

	public readonly ConfigEntry<bool> RequireArtifact;

	public readonly ConfigEntry<int> LadderBase;

	public readonly ConfigEntry<int> MaxExponent;

	public readonly ConfigEntry<int> MaxItems;

	public readonly ConfigEntry<bool> ScaleTier1;

	public readonly ConfigEntry<bool> ScaleTier2;

	public readonly ConfigEntry<bool> ScaleTier3;

	public readonly ConfigEntry<bool> ScaleBoss;

	public readonly ConfigEntry<bool> ScaleLunar;

	public readonly ConfigEntry<bool> ScaleVoid;

	public readonly ConfigEntry<bool> ScaleFood;

	public readonly ConfigEntry<bool> ScaleTierless;

	public readonly ConfigEntry<bool> ScaleTemporary;

	public readonly ConfigEntry<bool> ScaleModdedTiers;

	public readonly ConfigEntry<string> BlockedItems;

	public readonly ConfigEntry<bool> ShowTooltip;

	public readonly ConfigEntry<bool> DebugLogging;

	/// <summary>Bumped when parsed rules change, so caches can invalidate cheaply.</summary>
	public int Revision { get; private set; }

	/// <summary>
	/// True when an existing config file predated the "Require Artifact" key. BepInEx gives a
	/// newly added key its default, so upgrading from 1.0.x silently switches scaling behind
	/// the artifact. The plugin shouts about this rather than letting it look like a bug.
	/// </summary>
	public bool UpgradedFromPreArtifactConfig { get; private set; }

	public ExponentialModsConfig(ConfigFile config, ManualLogSource log)
	{
		_log = log;
		UpgradedFromPreArtifactConfig = DetectPreArtifactConfig(config);

		Enabled = config.Bind(SectionLadder, "Enabled", true,
			"Master switch. When OFF the mod does nothing and items stack the vanilla +1 per pickup.");

		RequireArtifact = config.Bind(SectionLadder, "Require Artifact", true,
			"When ON (default), the ladder only applies while \"Artifact of Exponents\" is enabled for the run, " +
			"so you can turn scaling on and off from the artifact list in the lobby. " +
			"When OFF, the ladder is always active and the artifact does nothing.");

		LadderBase = config.Bind(SectionLadder, "Base", 2,
			new ConfigDescription(
				"The base of the power ladder. Every item's stack climbs base^n, one rung per pickup.\n" +
				"  2  ->  1, 2, 4, 8, 16, 32, 64, ...\n" +
				"  3  ->  1, 3, 9, 27, 81, 243, ...\n" +
				"  10 ->  1, 10, 100, 1000, ...\n" +
				"Higher bases hit the item ceiling in very few pickups.",
				new AcceptableValueRange<int>(ExponentialLadder.MinBase, ExponentialLadder.MaxBase)));

		MaxExponent = config.Bind(SectionLimits, "Max Exponent", 0,
			new ConfigDescription(
				"Highest exponent n the ladder is allowed to reach, so the top rung is base^n.\n" +
				"0 = automatic: use the largest n where base^n still fits inside Max Items.\n" +
				"With Base 2 and the default Max Items that is n = 24 (16,777,216).",
				new AcceptableValueRange<int>(0, 31)));

		MaxItems = config.Bind(SectionLimits, "Max Items", ExponentialLadder.SafeStackDefault,
			new ConfigDescription(
				"Ceiling on the stack count this mod will ever grant for a single item.\n" +
				"The ladder's top rung is the largest base^n that fits underneath this value, so with a " +
				"ceiling that is not itself a power of the base the ladder settles a little below it " +
				"(base 3 under 16,777,216 tops out at 3^15 = 14,348,907). Once there, the mod stops " +
				"adding stacks and pickups revert to the vanilla +1 -- it will not take items away.\n" +
				"Risk of Rain 2 stores item stacks in a 32-bit signed int and clamps a single stack at " +
				"2,147,483,647, which is the absolute maximum this can be set to.\n" +
				"The default of 16,777,216 (2^24) is the recommended value: it is the largest integer a " +
				"32-bit float represents exactly, and it leaves headroom for two places where vanilla " +
				"still adds stacks in unchecked int arithmetic (Inventory.UpdateEffectiveItemStacks " +
				"combining permanent/channeled/temporary copies, and Inventory.GetTotalItemCountOfTier " +
				"summing a whole tier). Raising this toward int.MaxValue makes those overflow.",
				new AcceptableValueRange<int>(1, ExponentialLadder.RoR2StackHardLimit)));

		ScaleTier1 = config.Bind(SectionFilters, "Scale White Items", true, "Scale white (common) items.");
		ScaleTier2 = config.Bind(SectionFilters, "Scale Green Items", true, "Scale green (uncommon) items.");
		ScaleTier3 = config.Bind(SectionFilters, "Scale Red Items", true, "Scale red (legendary) items.");
		ScaleBoss = config.Bind(SectionFilters, "Scale Boss Items", true, "Scale boss (yellow) items.");
		ScaleLunar = config.Bind(SectionFilters, "Scale Lunar Items", true, "Scale lunar (blue) items.");
		ScaleVoid = config.Bind(SectionFilters, "Scale Void Items", true, "Scale void-tier items.");
		ScaleFood = config.Bind(SectionFilters, "Scale Food Items", true, "Scale Seekers of the Storm food-tier items.");
		ScaleTierless = config.Bind(SectionFilters, "Scale No-Tier Items", false,
			"Scale hidden / no-tier items. These are usually internal bookkeeping items; off by default.");
		ScaleTemporary = config.Bind(SectionFilters, "Scale Temporary Items", false,
			"Scale runtime temporary items. Off by default.");
		ScaleModdedTiers = config.Bind(SectionFilters, "Scale Unknown / Modded Tiers", true,
			"Scale items whose tier comes from another mod and matches none of the tiers above.");

		BlockedItems = config.Bind(SectionFilters, "Never Scale These Items", string.Empty,
			"Comma or semicolon separated list of item INTERNAL names to leave at vanilla +1 " +
			"(for example: AutoCastEquipment, Clover, Behemoth). Internal names, not display names.");

		ShowTooltip = config.Bind(SectionLadder, "Show Ladder In Item Descriptions", true,
			"Append a one-line summary of the ladder to item descriptions.");

		DebugLogging = config.Bind("Debug", "Verbose Logging", false, "Log every scaled pickup.");

		BlockedItems.SettingChanged += delegate
		{
			RebuildBlockList();
		};
		LadderBase.SettingChanged += delegate
		{
			Revision++;
		};
		MaxExponent.SettingChanged += delegate
		{
			Revision++;
		};
		MaxItems.SettingChanged += delegate
		{
			Revision++;
		};
	}

	/// <summary>Effective ceiling: never above what Risk of Rain 2 will actually store.</summary>
	public int EffectiveCeiling => Math.Min(Math.Max(1, MaxItems.Value), ExponentialLadder.RoR2StackHardLimit);

	public int EffectiveBase => ExponentialLadder.ClampBase(LadderBase.Value);

	public int GetNextRung(int currentStack)
	{
		return ExponentialLadder.GetNextRung(currentStack, EffectiveBase, MaxExponent.Value, EffectiveCeiling);
	}

	public string DescribeLadder()
	{
		return ExponentialLadder.DescribeLadder(EffectiveBase, MaxExponent.Value, EffectiveCeiling);
	}

	private static bool DetectPreArtifactConfig(ConfigFile config)
	{
		try
		{
			string path = config.ConfigFilePath;
			return !string.IsNullOrEmpty(path) && File.Exists(path)
				&& File.ReadAllText(path).IndexOf("Require Artifact", StringComparison.Ordinal) < 0;
		}
		catch
		{
			return false;
		}
	}

	public void RebuildBlockList()
	{
		Revision++;
		_blockedIndices.Clear();
		_catalogReady = false;
		string raw = BlockedItems.Value;
		if (string.IsNullOrWhiteSpace(raw))
		{
			_catalogReady = ItemCatalog.itemCount > 0;
			return;
		}
		int resolved = 0;
		foreach (string token in raw.Split(new char[4] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string name = token.Trim();
			if (name.Length == 0)
			{
				continue;
			}
			ItemIndex index = ItemCatalog.FindItemIndex(name);
			if (index == ItemIndex.None)
			{
				_log.LogWarning("[Never Scale These Items] Unknown or not-yet-loaded item name: '" + name + "'");
				continue;
			}
			_blockedIndices.Add((int)index);
			resolved++;
		}
		// Only latch as ready once the catalog actually existed, so an early parse during
		// Awake does not permanently record "nothing resolved".
		_catalogReady = ItemCatalog.itemCount > 0;
		if (DebugLogging.Value)
		{
			_log.LogInfo($"Block list rebuilt: {resolved} item(s).");
		}
	}

	public bool IsBlocked(ItemDef itemDef)
	{
		if ((Object)(object)itemDef == (Object)null)
		{
			return true;
		}
		if (!_catalogReady)
		{
			RebuildBlockList();
		}
		return _blockedIndices.Count > 0 && _blockedIndices.Contains((int)itemDef.itemIndex);
	}

	public bool IsTierAllowed(ItemDef itemDef)
	{
		switch (itemDef.tier)
		{
		case ItemTier.Tier1:
			return ScaleTier1.Value;
		case ItemTier.Tier2:
			return ScaleTier2.Value;
		case ItemTier.Tier3:
			return ScaleTier3.Value;
		case ItemTier.Boss:
			return ScaleBoss.Value;
		case ItemTier.Lunar:
			return ScaleLunar.Value;
		case ItemTier.VoidTier1:
		case ItemTier.VoidTier2:
		case ItemTier.VoidTier3:
		case ItemTier.VoidBoss:
			return ScaleVoid.Value;
		case ItemTier.FoodTier:
			return ScaleFood.Value;
		case ItemTier.NoTier:
			return ScaleTierless.Value;
		case ItemTier.AssignedAtRuntime:
			return ScaleTemporary.Value;
		default:
			return ScaleModdedTiers.Value;
		}
	}
}
