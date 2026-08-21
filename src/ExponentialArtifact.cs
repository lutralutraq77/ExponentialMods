using System;
using System.IO;
using BepInEx.Logging;
using R2API;
using RoR2;
using UnityEngine;
using Object = UnityEngine.Object;
using Path = System.IO.Path;

namespace ExponentialMods;

/// <summary>How the artifact gate currently stands.</summary>
internal enum ArtifactGateState
{
	/// <summary>No usable artifact — the caller should fail open (scale anyway).</summary>
	Unavailable,

	/// <summary>Registered and catalogued, but not enabled for this run.</summary>
	Off,

	/// <summary>Registered, catalogued and enabled for this run.</summary>
	On,
}

/// <summary>
/// Registers "Artifact of Exponents", the in-game on/off switch for the ladder.
///
/// The artifact is always registered so it appears in the lobby's artifact list; whether it
/// actually gates scaling is decided by the "Require Artifact" config key.
/// </summary>
internal static class ExponentialArtifact
{
	public const string NameToken = "ARTIFACT_EXPONENTIALMODS_NAME";

	public const string DescriptionToken = "ARTIFACT_EXPONENTIALMODS_DESC";

	private static ArtifactDef _def;

	/// <summary>Set only when ContentAddition actually accepted the def.</summary>
	private static bool _accepted;

	public static void Register(string pluginDirectory, ManualLogSource log)
	{
		if (_accepted)
		{
			return;
		}
		try
		{
			LanguageAPI.Add(NameToken, "Artifact of Exponents");
			LanguageAPI.Add(DescriptionToken, "Item stacks climb a power ladder instead of one at a time.");

			_def = ScriptableObject.CreateInstance<ArtifactDef>();
			_def.cachedName = "ExponentialModsExponents";
			_def.nameToken = NameToken;
			_def.descriptionToken = DescriptionToken;
			// ArtifactDef.artifactIndex is a plain auto-property, so it defaults to 0 -- a
			// VALID catalog index -- rather than ArtifactIndex.None (-1). If this def never
			// reaches the catalog, an unguarded IsArtifactEnabled(def) would silently read
			// the on/off state of whichever artifact occupies slot 0. Start at None so a
			// stale index can never masquerade as ours.
			_def.artifactIndex = ArtifactIndex.None;

			// Icon problems must not cost us the artifact: build the sprites in their own
			// try so a decode failure degrades to the flat fallback instead of aborting
			// registration entirely.
			BuildIcons(pluginDirectory, log, out var selected, out var deselected);
			_def.smallIconSelectedSprite = selected;
			_def.smallIconDeselectedSprite = deselected;

			// AddArtifactDef returns false (and only logs) when an icon is null or the
			// ArtifactCatalog has already initialised. Dropping that result would leave
			// _def assigned and make us report a success we did not achieve.
			if (!ContentAddition.AddArtifactDef(_def))
			{
				_def = null;
				_accepted = false;
				log.LogError("ContentAddition rejected Artifact of Exponents. Continuing without it: scaling will be always-on.");
				return;
			}
			_accepted = true;
			log.LogInfo("Registered Artifact of Exponents.");
		}
		catch (Exception ex)
		{
			_def = null;
			_accepted = false;
			log.LogError("Failed to register Artifact of Exponents, continuing without it: " + ex);
		}
	}

	/// <summary>
	/// The gate's current state. <see cref="ArtifactGateState.Unavailable"/> means callers
	/// should fail open rather than disable scaling — a stacking mod that silently does
	/// nothing is the worst possible failure.
	/// </summary>
	public static ArtifactGateState GetState()
	{
		try
		{
			if (!_accepted || (Object)(object)_def == (Object)null)
			{
				return ArtifactGateState.Unavailable;
			}
			// Confirm the catalog really points back at this def before trusting the index.
			ArtifactIndex index = _def.artifactIndex;
			if (index == ArtifactIndex.None || (Object)(object)ArtifactCatalog.GetArtifactDef(index) != (Object)(object)_def)
			{
				return ArtifactGateState.Unavailable;
			}
			RunArtifactManager instance = RunArtifactManager.instance;
			if ((Object)(object)instance == (Object)null)
			{
				// No run in progress (main menu, lobby, logbook).
				return ArtifactGateState.Off;
			}
			return instance.IsArtifactEnabled(_def) ? ArtifactGateState.On : ArtifactGateState.Off;
		}
		catch
		{
			return ArtifactGateState.Unavailable;
		}
	}

	private static void BuildIcons(string pluginDirectory, ManualLogSource log, out Sprite selected, out Sprite deselected)
	{
		selected = null;
		deselected = null;
		try
		{
			Texture2D icon = TryLoadTexture(pluginDirectory, log, "icon.png");
			if ((Object)(object)icon != (Object)null)
			{
				selected = MakeSprite(icon, "exponentialmods_artifact_on");
				deselected = MakeSprite(Darken(icon, 0.45f), "exponentialmods_artifact_off");
			}
		}
		catch (Exception ex)
		{
			log.LogWarning("Artifact icon could not be prepared, using a plain fallback: " + ex.Message);
			selected = null;
			deselected = null;
		}
		// R2API rejects the def outright if either sprite is null, so always supply something.
		if ((Object)(object)selected == (Object)null)
		{
			selected = MakeFlatSprite(new Color(0.84f, 0.71f, 1f, 1f), "exponentialmods_artifact_on");
		}
		if ((Object)(object)deselected == (Object)null)
		{
			deselected = MakeFlatSprite(new Color(0.22f, 0.22f, 0.28f, 1f), "exponentialmods_artifact_off");
		}
	}

	private static Texture2D TryLoadTexture(string directory, ManualLogSource log, string fileName)
	{
		if (string.IsNullOrEmpty(directory))
		{
			return null;
		}
		string path = Path.Combine(directory, fileName);
		if (!File.Exists(path))
		{
			log.LogWarning("Artifact icon not found next to the plugin (" + path + "); using a plain fallback.");
			return null;
		}
		Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
		if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
		{
			log.LogWarning("Artifact icon could not be decoded: " + path);
			return null;
		}
		return tex;
	}

	private static Sprite MakeSprite(Texture2D texture, string name)
	{
		((Object)texture).name = name;
		return Sprite.Create(texture, new Rect(0f, 0f, ((Texture)texture).width, ((Texture)texture).height), new Vector2(0.5f, 0.5f), 100f);
	}

	private static Texture2D Darken(Texture2D source, float multiplier)
	{
		int w = ((Texture)source).width;
		int h = ((Texture)source).height;
		Texture2D copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
		Color[] pixels = source.GetPixels();
		float m = Mathf.Clamp01(multiplier);
		for (int i = 0; i < pixels.Length; i++)
		{
			Color c = pixels[i];
			pixels[i] = new Color(c.r * m, c.g * m, c.b * m, c.a);
		}
		copy.SetPixels(pixels);
		copy.Apply();
		return copy;
	}

	private static Sprite MakeFlatSprite(Color color, string name)
	{
		Texture2D tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
		Color[] pixels = new Color[64 * 64];
		for (int i = 0; i < pixels.Length; i++)
		{
			pixels[i] = color;
		}
		tex.SetPixels(pixels);
		tex.Apply();
		return MakeSprite(tex, name);
	}
}
