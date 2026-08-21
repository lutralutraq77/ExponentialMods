using System;

namespace ExponentialMods;

/// <summary>
/// The whole scaling model: item stacks climb a pure power ladder.
///
/// With <c>base = 2</c> the rungs are 1, 2, 4, 8, 16, ... (2^n).
/// With <c>base = 3</c> they are 1, 3, 9, 27, 81, ... (3^n).
/// Each qualifying pickup moves the stack to the next rung above where it currently sits.
///
/// Everything is computed in <see cref="long"/> and saturated, never cast blindly, because
/// the destination is a 32-bit signed field (see <see cref="RoR2StackHardLimit"/>).
/// </summary>
internal static class ExponentialLadder
{
	/// <summary>
	/// The true hard ceiling for a single item stack in Risk of Rain 2.
	///
	/// <c>Inventory.ChangeItemStacksCount</c> widens the running total to <c>long</c> and
	/// clamps it with <c>Math.Clamp(count + countToAdd, 0L, 2147483647L)</c>, so the game
	/// saturates a single stack at int.MaxValue rather than wrapping. Anything this mod
	/// asks for above this value is silently discarded by the game.
	/// </summary>
	public const int RoR2StackHardLimit = int.MaxValue;

	/// <summary>
	/// Default ceiling, and the highest value that is safe rather than merely accepted.
	///
	/// Two vanilla behaviours bite well before int.MaxValue:
	///
	/// 1. <c>Inventory.UpdateEffectiveItemStacks</c> adds the permanent, channeled and temp
	///    stacks of one item together in plain <c>int</c> arithmetic and only clamps the
	///    result afterwards, so a permanent stack close to int.MaxValue that also picks up
	///    a temporary copy wraps negative and clamps to 0 -- the stack vanishes.
	/// 2. <c>Inventory.GetTotalItemCountOfTier</c> sums every stack in a tier into a plain
	///    <c>int</c> with no clamp at all.
	///
	/// 2^24 is also the largest integer a 32-bit float represents exactly, so stat math
	/// built on stack counts stays exact up to this point.
	/// </summary>
	public const int SafeStackDefault = 16777216;

	public const int MinBase = 2;

	/// <summary>Base 64 already reaches the hard limit at n = 5; anything larger is noise.</summary>
	public const int MaxBase = 64;

	/// <summary>
	/// Largest exponent n for which <c>ladderBase^n &lt;= ceiling</c>.
	/// Computed by repeated multiplication in long -- Math.Log would misjudge the boundary
	/// cases (e.g. log(1000)/log(10) landing just under 3).
	/// </summary>
	public static int MaxSafeExponent(int ladderBase, int ceiling)
	{
		ladderBase = ClampBase(ladderBase);
		if (ceiling < 1)
		{
			return 0;
		}
		int exponent = 0;
		long value = 1L;
		while (true)
		{
			long next = value * ladderBase;
			if (next > ceiling)
			{
				return exponent;
			}
			value = next;
			exponent++;
		}
	}

	/// <summary><c>ladderBase^exponent</c>, saturating at <paramref name="ceiling"/>.</summary>
	public static int Pow(int ladderBase, int exponent, int ceiling)
	{
		ladderBase = ClampBase(ladderBase);
		ceiling = ClampCeiling(ceiling);
		if (exponent <= 0)
		{
			return 1;
		}
		long value = 1L;
		for (int i = 0; i < exponent; i++)
		{
			value *= ladderBase;
			if (value >= ceiling)
			{
				return ceiling;
			}
		}
		return (int)value;
	}

	/// <summary>
	/// The stack this pickup should land on: the lowest rung strictly above
	/// <paramref name="currentStack"/>, capped by both the exponent limit and the ceiling.
	/// </summary>
	public static int GetNextRung(int currentStack, int ladderBase, int maxExponent, int ceiling)
	{
		ladderBase = ClampBase(ladderBase);
		ceiling = ClampCeiling(ceiling);

		int exponentLimit = MaxSafeExponent(ladderBase, ceiling);
		if (maxExponent > 0 && maxExponent < exponentLimit)
		{
			exponentLimit = maxExponent;
		}
		int topRung = Pow(ladderBase, exponentLimit, ceiling);

		if (currentStack < 0)
		{
			currentStack = 0;
		}
		if (currentStack >= topRung)
		{
			// Already at or above the top of the ladder: hold there rather than walking the
			// stack back down, which would mean destroying items the player owns.
			return Math.Max(currentStack, topRung);
		}

		long rung = 1L;
		for (int i = 0; i <= exponentLimit; i++)
		{
			if (rung > currentStack)
			{
				return (int)Math.Min(rung, topRung);
			}
			rung *= ladderBase;
		}
		return topRung;
	}

	public static int ClampBase(int ladderBase)
	{
		if (ladderBase < MinBase)
		{
			return MinBase;
		}
		if (ladderBase > MaxBase)
		{
			return MaxBase;
		}
		return ladderBase;
	}

	public static int ClampCeiling(int ceiling)
	{
		if (ceiling < 1)
		{
			return 1;
		}
		// Never exceed what the game itself will store.
		return ceiling;
	}

	/// <summary>Renders the first few rungs, for config help text and tooltips.</summary>
	public static string DescribeLadder(int ladderBase, int maxExponent, int ceiling, int rungs = 8)
	{
		ladderBase = ClampBase(ladderBase);
		ceiling = ClampCeiling(ceiling);
		int exponentLimit = MaxSafeExponent(ladderBase, ceiling);
		if (maxExponent > 0 && maxExponent < exponentLimit)
		{
			exponentLimit = maxExponent;
		}
		int shown = Math.Min(rungs, exponentLimit + 1);
		string[] parts = new string[shown];
		for (int i = 0; i < shown; i++)
		{
			parts[i] = Pow(ladderBase, i, ceiling).ToString();
		}
		string text = string.Join(", ", parts);
		if (shown <= exponentLimit)
		{
			text = text + ", ... , " + Pow(ladderBase, exponentLimit, ceiling);
		}
		return text;
	}
}
