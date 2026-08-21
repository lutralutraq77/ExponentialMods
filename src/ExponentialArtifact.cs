using System;
using System.IO;
using BepInEx.Logging;
using R2API;
using RoR2;
using UnityEngine;
using Object = UnityEngine.Object;
using Path = System.IO.Path;

namespace ExponentialMods;

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

	public static bool IsRegistered => (Object)(object)_def != (Object)null;

	public static void Register(string pluginDirectory, ManualLogSource log)
	{
		if (IsRegistered)
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

			Texture2D icon = TryLoadTexture(pluginDirectory, log, "icon.png");
			_def.smallIconSelectedSprite = ((Object)(object)icon != (Object)null)
				? MakeSprite(icon, "exponentialmods_artifact_on")
				: MakeFlatSprite(new Color(0.84f, 0.71f, 1f, 1f), "exponentialmods_artifact_on");
			_def.smallIconDeselectedSprite = ((Object)(object)icon != (Object)null)
				? MakeSprite(Darken(icon, 0.45f), "exponentialmods_artifact_off")
				: MakeFlatSprite(new Color(0.22f, 0.22f, 0.28f, 1f), "exponentialmods_artifact_off");

			ContentAddition.AddArtifactDef(_def);
			log.LogInfo("Registered Artifact of Exponents.");
		}
		catch (Exception ex)
		{
			// A failed artifact registration must never take the whole mod down: without it
			// IsEnabled() reports false, and the config guard falls back to always-on.
			_def = null;
			log.LogError("Failed to register Artifact of Exponents, continuing without it: " + ex);
		}
	}

	/// <summary>
	/// True when the artifact is active for the current run. Returns false when the artifact
	/// could not be registered or no run is in progress.
	/// </summary>
	public static bool IsEnabled()
	{
		try
		{
			if (!IsRegistered)
			{
				return false;
			}
			RunArtifactManager instance = RunArtifactManager.instance;
			if ((Object)(object)instance == (Object)null)
			{
				return false;
			}
			return instance.IsArtifactEnabled(_def);
		}
		catch
		{
			return false;
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
			return null;
		}
		try
		{
			Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
			{
				log.LogWarning("Artifact icon could not be decoded: " + path);
				return null;
			}
			return tex;
		}
		catch (Exception ex)
		{
			log.LogWarning("Artifact icon could not be read (" + path + "): " + ex.Message);
			return null;
		}
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
