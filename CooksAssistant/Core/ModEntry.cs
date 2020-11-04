﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CooksAssistant.GameObjects;
using CooksAssistant.GameObjects.Menus;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Netcode;
using SpaceCore;
using SpaceCore.Events;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using Object = StardewValley.Object;
using xTile.Dimensions;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

// TODO: CONTENT: ensure all slice/half objects have the correct category and colour/text overrides

namespace CooksAssistant
{
	public class ModEntry : Mod
	{
		internal static ModEntry Instance;
		internal Config Config;
		internal ModSaveData SaveData;
		internal CookingSkill CookingSkill;

		internal ITranslationHelper i18n => Helper.Translation;
		internal static IJsonAssetsApi JsonAssets;
		internal static Texture2D SpriteSheet;
		internal static CookingMenuButton CookingMenuButton;

		internal static readonly string ContentPackPath = Path.Combine("assets", "ContentPack");
		internal static readonly string SpriteSheetPath = Path.Combine("assets", "sprites");
		internal static readonly string BundleDataPath = Path.Combine("assets", "bundles");
		internal static readonly string BuffChartPath = Path.Combine("assets", "ingredientBuffChart");
		internal static readonly string SkillIconPath = Path.Combine("assets", "skill");
		internal static readonly string LevelUpIconPath = Path.Combine("assets", "levelup");

		internal const string SaveDataKey = "SaveData";
		internal const string AssetPrefix = "blueberry.CooksAssistant.";
		internal const string CookingSkillId = AssetPrefix + "CookingSkill";

		internal const string ActionDockCrate = AssetPrefix + "DockCrate";
		internal const string ActionRange = AssetPrefix + "Range";

		internal const string CommunityCentreAreaName = "Kitchen";
		internal const int CommunityCentreAreaNumber = 6;
		internal static readonly Rectangle CommunityCentreArea = new Rectangle(0, 0, 11, 11);
		internal static readonly Point CommunityCentreNotePosition = new Point(6, 7);
		internal int BundleStartIndex;

		internal const string DockCrateItem = "Pineapple";
		internal const string EasterEggItem = "Chocolate Egg";
		internal const string ChocolateBarItem = "Chocolate Bar";
		internal const string EasterBasketItem = "Egg Basket";
		internal static readonly string[] UntrashableItems = {
			EasterBasketItem
		};

		internal static readonly Location SaloonCookingRangePosition = new Location(16, 16);
		internal static readonly Dictionary<string, string> NpcHomeLocations = new Dictionary<string, string>();

		private const string KebabBuffSource = AssetPrefix + "Kebab";
		private const int KebabBonusDuration = 220;
		private const int KebabMalusDuration = 140;
		private const int KebabCombatBonus = 3;
		private const int KebabNonCombatBonus = 2;

		internal static KeyValuePair<string, string> TempPair;

		private string _cmd = "";
		
		private const float CombatRegenModifier = 0.02f;
		private const float CookingRegenModifier = 0.005f;
		private const float ForagingRegenModifier = 0.0012f;
		private float _healthOnLastTick, _staminaOnLastTick;
		private int _healthRegeneration, _staminaRegeneration;
		private float _debugRegenRate;
		private uint _debugElapsedTime, _debugTicksCurr;
		private Queue<uint> _debugTicksDiff = new Queue<uint>();
		private Object _lastFoodEaten;
		private bool _lastFoodWasDrink;
		internal static readonly Dictionary<int, int> FoodCookedToday = new Dictionary<int, int>();

		private Buff _watchingBuff;

		public override void Entry(IModHelper helper)
		{
			Instance = this;
			Config = helper.ReadConfig<Config>();
			_cmd = Config.ConsoleCommandPrefix;

			var assetManager = new AssetManager();
			Helper.Content.AssetEditors.Add(assetManager);

			SpriteSheet = Helper.Content.Load<Texture2D>($"{SpriteSheetPath}.png");
			
			Helper.Events.GameLoop.GameLaunched += GameLoopOnGameLaunched;
			Helper.Events.GameLoop.SaveLoaded += GameLoopOnSaveLoaded;
			Helper.Events.GameLoop.Saving += GameLoopOnSaving;
			Helper.Events.GameLoop.DayStarted += GameLoopOnDayStarted;
			Helper.Events.GameLoop.ReturnedToTitle += GameLoopOnReturnedToTitle;
			Helper.Events.GameLoop.UpdateTicked += GameLoopUpdateTicked;
			Helper.Events.Player.Warped += PlayerOnWarped;
			Helper.Events.Input.ButtonPressed += InputOnButtonPressed;

			if (Config.CookingOverhaul)
			{
				Helper.Events.Display.MenuChanged += DisplayOnMenuChanged;
			}
			if (Config.CookingSkill)
			{
				CookingSkill = new CookingSkill();
				Skills.RegisterSkill(CookingSkill);
			}
			if (Config.DebugMode)
			{
				Helper.Events.Display.RenderedHud += Event_DrawDebugHud;
			}
			SpaceEvents.OnItemEaten += SpaceEventsOnItemEaten;
			SpaceEvents.BeforeGiftGiven += SpaceEventsOnBeforeGiftGiven;

			HarmonyPatches.Patch();

			Helper.ConsoleCommands.Add(_cmd + "menu", "Open cooking menu.", (s, args)
				=> { OpenNewCookingMenu(); });
			Helper.ConsoleCommands.Add(_cmd + "lvl", "Set cooking level.", (s, args)
				=>
			{
				if (!Config.CookingSkill)
				{
					Log.D("Cooking skill is not enabled.");
					return;
				}
				if (args.Length < 1)
					return;

				Skills.AddExperience(Game1.player, CookingSkillId,
					-1 * Skills.GetExperienceFor(Game1.player, CookingSkillId));
				for (var i = 0; i < int.Parse(args[0]); ++i)
					Skills.AddExperience(Game1.player, CookingSkillId, CookingSkill.ExperienceCurve[i]);
				foreach (var profession in CookingSkill.Professions)
					if (Game1.player.professions.Contains(profession.GetVanillaId()))
						Game1.player.professions.Remove(profession.GetVanillaId());
				Log.D($"Set Cooking skill to {Skills.GetSkillLevel(Game1.player, CookingSkillId)}");
			});
			Helper.ConsoleCommands.Add(_cmd + "lvlmenu", "Show cooking level menu.", (s, args) =>
			{
				if (!Config.CookingSkill)
				{
					Log.D("Cooking skill is not enabled.");
					return;
				}
				Helper.Reflection.GetMethod(CookingSkill, "showLevelMenu").Invoke(
					null, new EventArgsShowNightEndMenus());
				Log.D("Bumped Cooking skill levelup menu.");
			});
			Helper.ConsoleCommands.Add(_cmd + "tired", "Reduce health and stamina. Pass zero, one, or two values.",
				(s, args) =>
				{
					if (args.Length < 1)
					{
						Game1.player.health = Game1.player.maxHealth / 10;
						Game1.player.Stamina = Game1.player.MaxStamina / 10;
					}
					else
					{
						Game1.player.health = int.Parse(args[0]);
						Game1.player.Stamina = args.Length < 2 ? Game1.player.health * 2.5f : int.Parse(args[1]);
					}
					Log.D($"Set HP: {Game1.player.health}, EP: {Game1.player.Stamina}");
				});
		}

		internal void UpdateCommunityCentreData(CommunityCenter cc)
		{
			AppendAreasCompleteData(cc);// oh my god5
			AppendBundleData(cc);
		}

		/// <summary>
		/// 
		/// </summary>
		internal void AppendAreasCompleteData(CommunityCenter cc)
		{
			// fUCK YOUJ
			var areasComplete = Helper.Reflection
				.GetField<NetArray<bool, NetBool>>(cc, nameof(cc.areasComplete));
			var oldAreas = areasComplete.GetValue();
			var newAreas = new NetArray<bool, NetBool>(7);
			
			for (var i = 0; i < oldAreas.Count; ++i)
				newAreas[i] = oldAreas[i];
			newAreas[newAreas.Length - 1] = SaveData?.HasCompletedCookingBundle ?? false;
			areasComplete.SetValue(newAreas); // cunsn
		}

		/// <summary>
		/// This method is needed to update the CC bundle dictionary that's otherwise populated without our values.
		/// The CC constructor seemingly populates the dictionary without our changes to Data/Bundles, so it's topped up here.
		/// </summary>
		internal void AppendBundleData(CommunityCenter cc)
		{
			var brokenDictField = Helper.Reflection.GetField<Dictionary<int, int>>(cc, "bundleToAreaDictionary");
			var brokenDict = brokenDictField.GetValue();
			var keys = Game1.netWorldState.Value.BundleRewards.Keys.Where
				(key => !brokenDict.ContainsKey(key) && key > BundleStartIndex && BundleStartIndex > 0);
			var keysDict = keys.ToDictionary(key => key, value => CommunityCentreAreaNumber);
			brokenDict = brokenDict.Concat(keysDict).ToDictionary(pair => pair.Key, pair => pair.Value);
			brokenDictField.SetValue(brokenDict);

			if (!Config.DebugMode)
				return;
			
			// aauugh
			var dog = Game1.netWorldState.Value.Bundles;
			var dogTreats = Game1.netWorldState.Value.BundleRewards;
			Log.D(dog.Aggregate("dog: ", (s, boolses)
				=> boolses?.Count > 0 ? $"{s}\n{boolses.Aggregate("", (s1, pair) => $"{s1}\n{pair.Key}: {pair.Value.Aggregate("", (s2, b) => $"{s2} {b}")}")}" : "none"));
			Log.D(dogTreats.Aggregate("dogTreats: ", (s, boolses)
				=> boolses?.Count > 0 ? $"{s}\n{boolses.Aggregate("", (s1, pair) => $"{s1}\n{pair.Key}: {pair.Value}")}" : "none"));
			Log.D(cc.areasComplete.Aggregate("AreasComplete: ", (s, b) => $"{s} {b}"));
			Log.D(brokenDict.Aggregate("bundleToAreaDictionary: ", (s, pair) => $"{s} ({pair.Key}:{pair.Value})"));
		}
		/*
		internal void PretendCheckForMissingRewards(CommunityCenter cc)
		{
			var hasUnclaimedRewards = false;
			cc.missedRewardsChest.Value.items.Clear();
			var rewards = new List<Item>();
			foreach (var key in cc.bundleRewards.Keys)
			{
				var bundleToAreaDictionary = Helper.Reflection.GetField<Dictionary<int, int>>
					(cc, "bundleToAreaDictionary").GetValue();
				var area = bundleToAreaDictionary[key];
				if (cc.bundleRewards[key] && cc.areasComplete.Count > area && cc.areasComplete[area])
				{
					hasUnclaimedRewards = true;
					rewards.Clear();
					JunimoNoteMenu.GetBundleRewards(area, rewards);
					foreach (var item in rewards)
					{
						cc.missedRewardsChest.Value.addItem(item);
					}
				}
			}

			var missedRewardsChestTile = Helper.Reflection.GetField<Vector2>
				(cc, "missedRewardsChestTile").GetValue();
			var multiplayer = Helper.Reflection.GetField<Multiplayer>
				(typeof(Game1), "multiplayer").GetValue();
			if (hasUnclaimedRewards && !cc.missedRewardsChestVisible.Value)
			{
				cc.showMissedRewardsChestEvent.Fire(true);
				multiplayer.broadcastSprites(cc,
					new TemporaryAnimatedSprite(Game1.random.NextDouble() < 0.5 ? 5 : 46,
					missedRewardsChestTile * 64f + new Vector2(16f, 16f), Color.White)
				{
					layerDepth = 1f
				});
			}
			else if (!hasUnclaimedRewards && cc.missedRewardsChestVisible.Value)
			{
				cc.showMissedRewardsChestEvent.Fire(false);
				multiplayer.broadcastSprites(cc,
					new TemporaryAnimatedSprite(Game1.random.NextDouble() < 0.5 ? 5 : 46,
						missedRewardsChestTile * 64f + new Vector2(16f, 16f), Color.White)
				{
					layerDepth = 1f
				});
			}
			cc.missedRewardsChestVisible.Value = hasUnclaimedRewards;
		}
		*/
		private void LoadApis()
		{
			JsonAssets = Helper.ModRegistry.GetApi<IJsonAssetsApi>("spacechase0.JsonAssets");
			if (JsonAssets == null)
			{
				Log.E("Can't access the Json Assets API. Is the mod installed correctly?");
				return;
			}
			JsonAssets.LoadAssets(Path.Combine(Helper.DirectoryPath, ContentPackPath));
		}
		
		private void GameLoopOnGameLaunched(object sender, GameLaunchedEventArgs e)
		{
			LoadApis();
		}

		private void GameLoopOnSaveLoaded(object sender, SaveLoadedEventArgs e)
		{
			SaveData = Helper.Data.ReadSaveData<ModSaveData>(SaveDataKey) ?? new ModSaveData();

			// Invalidate and reload assets requiring JA indexes
			Helper.Content.InvalidateCache(@"Data/ObjectInformation");
			Helper.Content.InvalidateCache(@"Data/CookingRecipes");
			// and you
			Helper.Content.InvalidateCache(@"Data/Bundles");
			
			// Add watcher to check for first-time Kitchen bundle completion
			if (Config.AddCookingToCommunityCentre)
			{
				Helper.Events.GameLoop.DayEnding += Event_WatchingKitchenBundle;
			}

			// Load default recipes
			foreach (var recipe in Config.DefaultUnlockedRecipes
				.Where(recipe => !Game1.player.cookingRecipes.ContainsKey(recipe)))
				Game1.player.cookingRecipes.Add(recipe, 0);

			// Populate NPC home locations for cooking range usage
			var npcData = Game1.content.Load<Dictionary<string, string>>("Data/NPCDispositions");
			NpcHomeLocations.Clear();
			foreach (var npc in npcData)
				NpcHomeLocations.Add(npc.Key, npc.Value.Split('/')[10].Split(' ')[0]);
		}
		
		private void GameLoopOnSaving(object sender, SavingEventArgs e)
		{
			Helper.Data.WriteSaveData(SaveDataKey, SaveData);
		}

		private void GameLoopOnDayStarted(object sender, DayStartedEventArgs e)
		{
			// Load contextual recipes
			if (Game1.player.knowsRecipe("Maki Roll") && !Game1.player.cookingRecipes.ContainsKey("Eel Sushi"))
				Game1.player.cookingRecipes.Add("Eel Sushi", 0);
			if (Game1.player.knowsRecipe("Omelet") && !Game1.player.cookingRecipes.ContainsKey("Quick Breakfast"))
				Game1.player.cookingRecipes.Add("Quick Breakfast", 0);
			if (Game1.player.knowsRecipe("Hearty Stew") && !Game1.player.cookingRecipes.ContainsKey("Dwarven Stew"))
				Game1.player.cookingRecipes.Add("Dwarven Stew", 0);

			// Clear daily cooking to free up Cooking experience gains
			if (Config.CookingSkill)
			{
				FoodCookedToday.Clear();
			}

			// Attempt to place a wild nettle as forage around other weeds
			if (Game1.currentSeason != "winter")
			{
				foreach (var l in new[] {"Mountain", "Forest", "Railroad", "Farm"})
				{
					var location = Game1.getLocationFromName(l);
					var tile = location.getRandomTile();
					location.Objects.TryGetValue(tile, out var o);
					tile = Utility.getRandomAdjacentOpenTile(tile, location);
					if (tile == Vector2.Zero || o == null || o.ParentSheetIndex < 312 || o.ParentSheetIndex > 322)
						continue;
					location.terrainFeatures.Add(tile, new CustomBush(tile, location, CustomBush.BushVariety.Nettle));
				}
			}

			// Purge old easter eggs when Summer begins
			if (Game1.dayOfMonth == 1 && Game1.currentSeason == "summer")
			{
				const string itemToPurge = EasterEggItem;
				const string itemToAdd = ChocolateBarItem;
				foreach (var chest in Game1.locations.SelectMany(
					l => l.Objects.SelectMany(dict => dict.Values.Where(
						o => o is Chest c && c.items.Any(i => i.Name == itemToPurge)))).Cast<Chest>())
				{
					var stack = 0;
					foreach (var item in chest.items.Where(i => i.Name == itemToPurge))
					{
						// TODO: TEST: Easter egg expiration on Summer 1
						stack += item.Stack;
						chest.items[chest.items.IndexOf(item)] = null;
					}
					chest.items.Add(new Object(JsonAssets.GetObjectId(itemToAdd), stack));
				}
			}

			// aauugh
			if (Config.AddCookingToCommunityCentre)
			{
				UpdateCommunityCentreData(Game1.getLocationFromName("CommunityCenter") as CommunityCenter);
			}
		}
		
		private void GameLoopOnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
		{
			// Remove Kitchen bundle watcher, assuming one exists
			if (Config.AddCookingToCommunityCentre)
			{
				Helper.Events.GameLoop.DayEnding -= Event_WatchingKitchenBundle;
			}
		}

		private void GameLoopUpdateTicked(object sender, UpdateTickedEventArgs e)
		{
			_healthOnLastTick = Game1.player.health;
			_staminaOnLastTick = Game1.player.Stamina;
		}
		
		private void Event_DrawDebugHud(object sender, RenderedHudEventArgs e)
		{
			for (var i = 0; i < _debugTicksDiff.Count; ++i)
				e.SpriteBatch.DrawString(
					Game1.smallFont,
					$"{(i == 0 ? "DIFF" : "      ")}   {_debugTicksDiff.ToArray()[_debugTicksDiff.Count - 1 - i]}",
					new Vector2(Game1.graphics.GraphicsDevice.Viewport.Width - 222, Game1.graphics.GraphicsDevice.Viewport.Height - 144 - i * 24),
					Color.White * ((_debugTicksDiff.Count - 1 - i + 1f) / (_debugTicksDiff.Count / 2f)));
			e.SpriteBatch.DrawString(
				Game1.smallFont,
				$"MOD  {(_debugRegenRate < 1 ? 0 :_debugElapsedTime % _debugRegenRate)}",
				new Vector2(Game1.graphics.GraphicsDevice.Viewport.Width - 222, Game1.graphics.GraphicsDevice.Viewport.Height - 120),
				Color.White);
			e.SpriteBatch.DrawString(
				Game1.smallFont,
				$"RATE {_debugRegenRate}",
				new Vector2(Game1.graphics.GraphicsDevice.Viewport.Width - 222, Game1.graphics.GraphicsDevice.Viewport.Height - 96),
				Color.White);
			e.SpriteBatch.DrawString(
				Game1.smallFont,
				$"HP+   {_healthRegeneration}",
				new Vector2(Game1.graphics.GraphicsDevice.Viewport.Width - 222, Game1.graphics.GraphicsDevice.Viewport.Height - 72),
				Color.White);
			e.SpriteBatch.DrawString(
				Game1.smallFont,
				$"EP+   {_staminaRegeneration}",
				new Vector2(Game1.graphics.GraphicsDevice.Viewport.Width - 222, Game1.graphics.GraphicsDevice.Viewport.Height - 48),
				Color.White);
		}

		private void Event_FoodRegeneration(object sender, UpdateTickedEventArgs e)
		{
			// TODO: TEST: Food regeneration rates at different health/stamina and skill levels (combat, foraging, cooking)
			if (PlayerAgencyLostCheck())
				return;
			if (Game1.player.health < 1 || _healthRegeneration < 1 && _staminaRegeneration < 1)
			{
				Helper.Events.GameLoop.UpdateTicked -= Event_FoodRegeneration;
				return;
			}

			var cookingLevel = Skills.GetSkillLevel(Game1.player, CookingSkillId);
			var baseRate = 128;
			var panicRate = (Game1.player.health * 3f + Game1.player.Stamina)
			                / (Game1.player.maxHealth * 3f + Game1.player.MaxStamina);
			var regenRate = GetRegenRate(_lastFoodEaten);
			var scaling =
				(Game1.player.CombatLevel * CombatRegenModifier
				   + (Config.CookingSkill ? cookingLevel * CookingRegenModifier : 0)
				   + Game1.player.ForagingLevel * ForagingRegenModifier)
				/ (10 * CombatRegenModifier
				   + (Config.CookingSkill ? 10 * CookingRegenModifier : 0)
				   + 10 * ForagingRegenModifier);
			var rate = (baseRate - baseRate * scaling) * regenRate * 100d;
			rate = Math.Floor(Math.Max(32 - cookingLevel * 1.75f, rate * panicRate));

			_debugRegenRate = (float) rate;
			_debugElapsedTime = e.Ticks;
			++_debugTicksCurr;

			if (_debugTicksCurr < rate)
				return;

			_debugTicksDiff.Enqueue(_debugTicksCurr);
			if (_debugTicksDiff.Count > 5)
				_debugTicksDiff.Dequeue();
			_debugTicksCurr = 0;

			if (_healthRegeneration > 0)
			{
				if (Game1.player.health < Game1.player.maxHealth)
					++Game1.player.health;
				--_healthRegeneration;
			}

			if (_staminaRegeneration > 0)
			{
				if (Game1.player.Stamina < Game1.player.MaxStamina)
					++Game1.player.Stamina;
				--_staminaRegeneration;
			}
		}
		
		private void Event_UndoGiftChanges(object sender, UpdateTickedEventArgs e)
		{
			// Reset unique easter gift dialogue after it's invoked
			Helper.Events.GameLoop.UpdateTicked -= Event_UndoGiftChanges;
			Game1.NPCGiftTastes[TempPair.Key] = TempPair.Value;
			Log.D($"Reverted gift taste dialogue to {TempPair.Value}");
			TempPair = new KeyValuePair<string, string>();
		}
		
		private void Event_WatchingBuffs(object sender, UpdateTickedEventArgs e)
		{
			if (Game1.buffsDisplay.food.source != _watchingBuff.source
			    && Game1.buffsDisplay.drink.source != _watchingBuff.source
			    && Game1.buffsDisplay.otherBuffs.All(buff => buff.source != _watchingBuff.source))
			{
				Helper.Events.GameLoop.UpdateTicked -= Event_WatchingBuffs;

				_watchingBuff = null;
			}
		}
		
		private void Event_WatchingKitchenBundle(object sender, DayEndingEventArgs e)
		{
			// Send mail when completing the Kitchen in the community centre
			var mailId = $"cc{CommunityCentreAreaName}";
			var cc = Game1.getLocationFromName("CommunityCenter") as CommunityCenter;
			if (!cc.areasComplete[CommunityCentreAreaNumber] || Game1.player.mailReceived.Contains(mailId))
				return;

			Game1.player.mailForTomorrow.Add(mailId + "%&NL&%");
			Game1.addMailForTomorrow($"{Helper.ModRegistry.ModID}.ccKitchenBundleComplete");
		}

		private void Event_MoveJunimo(object sender, UpdateTickedEventArgs e)
		{
			var cc = Game1.currentLocation as CommunityCenter;
			var p = CommunityCentreNotePosition;
			if (cc.characters.FirstOrDefault(c => c is Junimo j && j.whichArea.Value == CommunityCentreAreaNumber)
			    == null)
			{
				Log.E($"No junimo in area {CommunityCentreAreaNumber} to move!");
			}
			else
			{
				cc.characters.FirstOrDefault(c => c is Junimo j && j.whichArea.Value == CommunityCentreAreaNumber)
					.Position = new Vector2(p.X, p.Y + 2) * 64f;
				Log.W("Moving junimo");
			}
			Helper.Events.GameLoop.UpdateTicked -= Event_MoveJunimo;
		}

		private void InputOnButtonPressed(object sender, ButtonPressedEventArgs e)
		{
			if (PlayerAgencyLostCheck() || Game1.keyboardDispatcher.Subscriber != null)
				return;

			// debug test
			if (Config.DebugMode)
			{
				switch (e.Button)
				{
					case SButton.G:
						Game1.player.warpFarmer(Game1.currentLocation is CommunityCenter
							? new Warp(0, 0, "FarmHouse", 0, 0, false)
							: new Warp(0, 0, "CommunityCenter", 12, 6, false));
						return;
					case SButton.H:
						OpenNewCookingMenu();
						return;
					case SButton.F5:
						Game1.currentLocation.largeTerrainFeatures.Add(
							new Bush(e.Cursor.GrabTile, 1, Game1.currentLocation));
						return;
					case SButton.F6:
						Game1.currentLocation.terrainFeatures.Add(e.Cursor.GrabTile,
							new CustomBush(e.Cursor.GrabTile, Game1.currentLocation, CustomBush.BushVariety.Nettle));
						return;
					case SButton.F7:
						Game1.currentLocation.largeTerrainFeatures.Add(
							new CustomBush(e.Cursor.GrabTile, Game1.currentLocation, CustomBush.BushVariety.Redberry));
						return;
				}
			}

			// Menu interactions:
			if (CookingMenuButton != null)
			{
				if (e.Button.IsUseToolButton() && CookingMenuButton.isWithinBounds(
					(int)e.Cursor.ScreenPixels.X, (int)e.Cursor.ScreenPixels.Y))
				{
					if (CheckForNearbyCookingStation() == 0)
					{
						Game1.showRedMessage(i18n.Get("menu.cooking_station.none"));
					}
					else
					{
						Log.W($"Clicked the campfire icon");
						OpenNewCookingMenu();
					}
				}
			}
			// When holding an untrashable item, check if cursor is on trashCan or outside of the menu, then block it
			if ((Game1.activeClickableMenu is GameMenu || Game1.activeClickableMenu is ItemGrabMenu)
			    && UntrashableItems.Contains(Game1.player.CursorSlotItem?.Name))
			{
				if (Game1.activeClickableMenu != null
				    && ((Game1.activeClickableMenu is CraftingPage craftingMenu
				         && craftingMenu.trashCan.containsPoint((int) e.Cursor.ScreenPixels.X,
					         (int) e.Cursor.ScreenPixels.Y)
				         || (Game1.activeClickableMenu is InventoryPage inventoryMenu
				             && inventoryMenu.trashCan.containsPoint((int) e.Cursor.ScreenPixels.X,
					             (int) e.Cursor.ScreenPixels.Y)))
				        || !Game1.activeClickableMenu.isWithinBounds((int) e.Cursor.ScreenPixels.X,
					        (int) e.Cursor.ScreenPixels.Y)))
				{
					Log.D("Caught untrashable item trying to get trashed", Config.DebugMode);
					Helper.Input.Suppress(e.Button);
				}
			}

			// World interactions:
			if (Game1.currentBillboard != 0 || Game1.activeClickableMenu != null || Game1.menuUp // No menus
			    || !Game1.player.CanMove) // Player agency enabled
				return;

			var btn = e.Button;
			if (btn.IsActionButton())
			{
				var tile = Game1.currentLocation.Map.GetLayer("Buildings")
					.Tiles[(int)e.Cursor.GrabTile.X, (int)e.Cursor.GrabTile.Y];
				if (tile != null && Config.IndoorsTileIndexesThatActAsCookingStations.Contains(tile.TileIndex))
				{
					if (NpcHomeLocations.Any(pair => pair.Value == Game1.currentLocation.Name
					                                 && Game1.player.getFriendshipHeartLevelForNPC(pair.Key) >= 5)
					|| NpcHomeLocations.All(pair => pair.Value != Game1.currentLocation.Name))
					{
						Log.W($"Clicked the kitchen at {Game1.currentLocation.Name}");
						OpenNewCookingMenu();
					}
					else
					{
						var name = NpcHomeLocations.FirstOrDefault(pair => pair.Value == Game1.currentLocation.Name).Key;
						Game1.showRedMessage(i18n.Get("world.range_npc.rejected",
							new {
								name = Game1.getCharacterFromName(name).displayName
							}));
					}
					Helper.Input.Suppress(e.Button);

					return;
				}

				// Use tile actions in maps
				CheckTileAction(e.Cursor.GrabTile, Game1.currentLocation);
			}
		}

		private void DisplayOnMenuChanged(object sender, MenuChangedEventArgs e)
		{
			// Try to add the menu button for cooking
			//if (!(e.NewMenu is GameMenu))
				RemoveCookingMenuButton();

			if (e.NewMenu is CraftingPage cm)
			{
				var cooking = Helper.Reflection.GetField<bool>(cm, "cooking").GetValue();
				if (cooking)
				{
					cm.exitThisMenuNoSound();
					OpenNewCookingMenu();
				}
				return;
			}

			if (!(e.NewMenu is GameMenu) || e.OldMenu is GameMenu && e.NewMenu is GameMenu)
				return;

			return;
			CookingMenuButton = new CookingMenuButton();
			Game1.onScreenMenus.Add(CookingMenuButton);
		}
		
		private void PlayerOnWarped(object sender, WarpedEventArgs e)
		{/*
			if (e.NewLocation is CommunityCenter && !(e.OldLocation is CommunityCenter))
				Helper.Events.Display.RenderedWorld += Event_DrawCC;
			else if (e.OldLocation is CommunityCenter && !(e.NewLocation is CommunityCenter))
				Helper.Events.Display.RenderedWorld -= Event_DrawCC;
			*/
			// TODO: TEST: drawing final star with TASprite
			if (!(e.NewLocation is CommunityCenter))
				return;
			
			Helper.Events.GameLoop.UpdateTicked += Event_MoveJunimo;
			const int num = CommunityCentreAreaNumber;
			var cc = e.NewLocation as CommunityCenter; // fgs fds
			var count = Helper.Reflection.GetField<NetArray<bool, NetBool>>(
				cc, nameof(cc.areasComplete)).GetValue();
			Log.D($"CC areasComplete count: {count}");
			
			if (cc.areasComplete[CommunityCentreAreaNumber])
			{
				var multiplayer = Helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
				multiplayer.broadcastSprites(
					Game1.currentLocation,
					new TemporaryAnimatedSprite(
						"LooseSprites\\Cursors", 
						new Rectangle(354, 401, 7, 7), 
						9999, 1, 9999, 
						new Vector2(2096f, 344f), 
						false, false, 0.8f, 0f, Color.White,
						4f, 0f, 0f, 0f)
					{
						holdLastFrame = true
					});
			}
			if (cc.areAllAreasComplete())
			{
				return;
			}

			var c1 = cc.isJunimoNoteAtArea(num);
			var c2 = cc.shouldNoteAppearInArea(num);
			if (!c1 && c2)
			{
				Log.E("Adding junimo note manually");
				cc.addJunimoNote(num);
				Helper.Reflection.GetMethod(cc, "resetSharedState").Invoke();
				Helper.Reflection.GetMethod(cc, "resetLocalState").Invoke();
			}
			return;
			// TODO: TEST: Whether cc.resetState() methods will automatically cover loadArea for new area
			if (cc.areasComplete[CommunityCentreAreaNumber])
			{
				cc.loadArea(num);
			}
		}

		private void SpaceEventsOnItemEaten(object sender, EventArgs e)
		{
			if (!(Game1.player.itemToEat is Object food))
				return;

			var objectData = Game1.objectInformation[food.ParentSheetIndex].Split('/');
			_lastFoodWasDrink = objectData.Length > 6 && objectData[6] == "drink";
			_lastFoodEaten = food;

			Log.D($"Ate food: {food.Name}");
			Log.D($"Buffs: (food) {Game1.buffsDisplay.food?.displaySource} (drink) {Game1.buffsDisplay.drink?.displaySource}");
			if (Config.FoodHealsOverTime)
			{
				Helper.Events.GameLoop.UpdateTicked += Event_FoodRegeneration;
				Game1.player.health = (int)_healthOnLastTick;
				Game1.player.Stamina = _staminaOnLastTick;
				_healthRegeneration += food.healthRecoveredOnConsumption();
				_staminaRegeneration += food.staminaRecoveredOnConsumption();
			}
			else if (Config.CookingSkill
			         && Game1.player.HasCustomProfession(CookingSkill.Professions[(int) CookingSkill.ProfId.Restoration]))
			{
				Game1.player.health = (int) Math.Min(Game1.player.maxHealth,
					Game1.player.health + food.healthRecoveredOnConsumption() * (CookingSkill.RestorationAltValue / 100f));
				Game1.player.Stamina = (int) Math.Min(Game1.player.MaxStamina,
					Game1.player.Stamina + food.staminaRecoveredOnConsumption() * (CookingSkill.RestorationAltValue / 100f));
			}

			var lastBuff = _lastFoodWasDrink
				? Game1.buffsDisplay.drink
				: Game1.buffsDisplay.food;
			Log.D($"Last buff: {lastBuff?.displaySource ?? "null"} ({lastBuff?.source ?? "null"})"
			      + $" | Food: {food.DisplayName} ({food.Name})");
			// TODO: DEBUG: Replace || with && when finished
			if ((Config.CookingSkill
			    || Game1.player.HasCustomProfession(CookingSkill.Professions[(int) CookingSkill.ProfId.BuffDuration]))
			    && food.displayName == lastBuff?.displaySource)
			{
				var duration = lastBuff.millisecondsDuration;
				if (duration > 0)
				{
					var rate = (Game1.player.health + Game1.player.Stamina)
					               / (Game1.player.maxHealth + Game1.player.MaxStamina);
					duration += (int) Math.Floor(CookingSkill.BuffDurationValue * 1000 * rate);
					lastBuff.millisecondsDuration = duration;
				}
			}

			if (!SaveData.FoodsEaten.ContainsKey(food.Name))
				SaveData.FoodsEaten.Add(food.Name, 0);
			++SaveData.FoodsEaten[food.Name];

			if (Config.GiveLeftoversFromBigFoods && Config.FoodsThatGiveLeftovers.Contains(food.Name))
			{
				// TODO: TEST: adding leftovers to a full inventory
				var leftovers = new Object(
					JsonAssets.GetObjectId(
						Config.FoodsWithLeftoversGivenAsSlices.Any(f => food.Name.ToLower().EndsWith(f))
							? $"{food.Name} Slice" 
							: $"{food.Name} Half"), 
					1);
				if (Game1.player.couldInventoryAcceptThisItem(leftovers))
					Game1.player.addItemToInventory(leftovers);
				else
					Game1.currentLocation.dropObject(leftovers, Game1.player.GetDropLocation(),
						Game1.viewport, true, Game1.player);
			}

			if (food.Name == "Kebab")
			{
				var roll = Game1.random.NextDouble();
				Buff buff = null;
				var duration = -1;
				var message = "";
				if (roll < 0.06f)
				{
					if (Config.FoodHealsOverTime)
					{
						_healthRegeneration -= food.healthRecoveredOnConsumption();
						_staminaRegeneration -= food.staminaRecoveredOnConsumption();
					}
					else
					{
						Game1.player.health = (int)_healthOnLastTick;;
						Game1.player.Stamina = _staminaOnLastTick;
					}
					message = i18n.Get("item.kebab.bad");

					if (roll < 0.03f)
					{
						var stats = new[] {0, 0, 0, 0};
						stats[Game1.random.Next(stats.Length - 1)] = KebabNonCombatBonus * -1;

						message = i18n.Get("item.kebab.worst");
						var displaySource = i18n.Get("buff.kebab.inspect",
							new {quality = i18n.Get("buff.kebab.quality_worst")});
						duration = KebabMalusDuration;
						buff = roll < 0.0125f
							? new Buff(stats[0], stats[1], stats[2], 0, 0, stats[3],
								0, 0, 0, 0, 0, 0,
								duration, KebabBuffSource, displaySource)
							: new Buff(0, 0, 0, 0, 0, 0,
								0, 0, 0, 0,
								KebabCombatBonus * -1, KebabCombatBonus * -1,
								duration, KebabBuffSource, displaySource);
					}
				}
				else if (roll < 0.18f)
				{
					if (Config.FoodHealsOverTime)
					{
						_healthRegeneration += Game1.player.maxHealth / 10;
						_staminaRegeneration += Game1.player.MaxStamina / 10;
					}
					else
					{
						Game1.player.health = Math.Min(Game1.player.maxHealth,
							Game1.player.health + Game1.player.maxHealth / 10);
						Game1.player.Stamina = Math.Min(Game1.player.MaxStamina,
							Game1.player.Stamina + Game1.player.MaxStamina / 10f);
					}

					var displaySource = i18n.Get("buff.kebab.inspect",
						new {quality = i18n.Get("buff.kebab.quality_best")});
					message = i18n.Get("item.kebab.best");
					duration = KebabBonusDuration;
					buff = new Buff(0, 0, KebabNonCombatBonus, 0, 0, 0,
						0, 0, 0, 0,
						KebabCombatBonus, KebabCombatBonus,
						duration, KebabBuffSource, displaySource);
				}
				if (string.IsNullOrEmpty(message))
					Game1.addHUDMessage(new HUDMessage(message));
				if (buff != null)
					Game1.buffsDisplay.tryToAddFoodBuff(buff, duration);
			}

			if ((!_lastFoodWasDrink && Game1.buffsDisplay.food?.source == food.Name)
			    || (_lastFoodWasDrink && Game1.buffsDisplay.drink?.source == food.Name))
			{
				// TODO: SYSTEM: Cooking Skill with added levels
				CookingSkill.AddedLevel = 0;
				Helper.Events.GameLoop.UpdateTicked += Event_WatchingBuffs;
			}
		}
		
		private void SpaceEventsOnBeforeGiftGiven(object sender, EventArgsBeforeReceiveObject e)
		{
			// Ignore when gifts aren't going to be accepted
			if (!Game1.player.friendshipData.ContainsKey(e.Npc.Name)
			    || Game1.player.friendshipData[e.Npc.Name].GiftsThisWeek > 1
			    || Game1.player.friendshipData[e.Npc.Name].GiftsToday > 0)
			{
				return;
			}

			// Cooking skill professions influence gift value of Cooking objects
			if (Config.CookingSkill
			    && Game1.player.HasCustomProfession(CookingSkill.Professions[(int) CookingSkill.ProfId.GiftBoost])
			    && e.Gift.Category == -7)
			{
				Game1.player.changeFriendship(CookingSkill.GiftBoostValue, e.Npc);
			}

			// Patch in unique gift dialogue for easter egg deliveries
			if (e.Gift.Name != EasterEggItem && e.Gift.Name != EasterBasketItem)
				return;
			if (e.Gift.Name == EasterBasketItem)
				++Game1.player.CurrentItem.Stack;
			
			TempPair = new KeyValuePair<string, string>(e.Npc.Name, Game1.NPCGiftTastes[e.Npc.Name]);
			var str = i18n.Get($"talk.egg_gift.{e.Npc.Name.ToLower()}");
			if (!str.HasValue())
				throw new KeyNotFoundException();
			Game1.NPCGiftTastes[e.Npc.Name] = UpdateEntry(
				Game1.NPCGiftTastes[e.Npc.Name], new[] {(string)str}, false, false, 2);

			// Remove the patch on the next tick, after the unique gift dialogue has been loaded and drawn
			Helper.Events.GameLoop.UpdateTicked += Event_UndoGiftChanges;
		}
		
		private bool PlayerAgencyLostCheck()
		{
			return !Game1.game1.IsActive // No alt-tabbed game state
			       || Game1.eventUp && !Game1.currentLocation.currentEvent.playerControlSequence // No event cutscenes
			       || Game1.nameSelectUp || Game1.IsChatting || Game1.dialogueTyping || Game1.dialogueUp // No text inputs
			       || Game1.player.UsingTool || Game1.pickingTool || Game1.numberOfSelectedItems != -1 // No tools in use
			       || Game1.fadeToBlack;
		}

		public void CheckTileAction(Vector2 position, GameLocation location)
		{
			var property = location.doesTileHaveProperty(
				(int) position.X, (int) position.Y, "Action", "Buildings");
			if (property == null)
				return;
			var action = property.Split(' ');
			switch (action[0])
			{
				case ActionRange:
					// A new cooking range in Gus' saloon acts as a cooking point
					if (Config.PlayWithQuestline && Game1.player.getFriendshipLevelForNPC("Gus") < 500)
					{
						CreateInspectDialogue(i18n.Get("world.range_gus.inspect"));
						break;
					}
					//Game1.activeClickableMenu = new CraftingPage(-1, -1, -1, -1, true);
					OpenNewCookingMenu();
					break;

				case ActionDockCrate:
					// Interact with the new crates at the secret beach pier to loot items for quests
					Game1.currentLocation.playSoundAt("ship", position);
					var roll = Game1.random.NextDouble();
					Object o = null;
					if (roll < 0.2f && Game1.player.eventsSeen.Contains(0))
					{
						o = new Object(JsonAssets.GetObjectId(DockCrateItem), 1);
						if (roll < 0.05f && Game1.player.eventsSeen.Contains(1))
							o = new Object(JsonAssets.GetObjectId("Chocolate Bar"), 1);
					}
					if (o != null)
						Game1.player.addItemByMenuIfNecessary(o.getOne());
					break;
			}
		}
		
		private void CreateInspectDialogue(string dialogue)
		{
			Game1.drawDialogueNoTyping(dialogue);
		}
		
		private void OpenNewCookingMenu()
		{
			Log.D("Opened cooking menu.");
			if (!(Game1.activeClickableMenu is CookingMenu)
			    || Game1.activeClickableMenu is CookingMenu menu && menu.PopMenuStack(true, true))
				Game1.activeClickableMenu = new CookingMenu();
		}
		
		internal static void RemoveCookingMenuButton()
		{
			foreach (var button in Game1.onScreenMenus.OfType<CookingMenuButton>().ToList())
			{
				Log.D($"Removing {nameof(button)}");
				Game1.onScreenMenus.Remove(button);
			}
			CookingMenuButton = null;
		}

		public float GetRegenRate(Object food)
		{
			// Regen faster when drinking
			var rate = _lastFoodWasDrink ? 0.2f : 0.15f;
			// Regen faster with quality
			rate += food.Quality * 0.008f;
			// Regen faster when drunk
			if (Game1.player.hasBuff(17))
				rate *= 1.3f;
			if (Config.CookingSkill && Game1.player.HasCustomProfession(
				CookingSkill.Professions[(int) CookingSkill.ProfId.Restoration]))
				rate += rate / CookingSkill.RestorationValue;
			return rate;
		}

		public int CheckForNearbyCookingStation()
		{
			var cookingStationLevel = 0;
			var range = int.Parse(Config.CookingStationUseRange);
			// If using Gus' cooking range, then use his own equipment level
			if (Game1.currentLocation.Name == "Saloon")
			{
				if (Utility.tileWithinRadiusOfPlayer(SaloonCookingRangePosition.X, SaloonCookingRangePosition.Y,
					range, Game1.player))
				{
					cookingStationLevel = Math.Max(SaveData.WorldGusCookingRangeLevel, SaveData.ClientCookingEquipmentLevel);
					Log.W($"Cooking station: {cookingStationLevel}");
				}
			}
			// If indoors, use the farmhouse or cabin level as a base for cooking level
			// A level 1 farmhouse has a maximum of 2 slots, and a farmhouse with a kitchen has a minimum of 2 slots
			else if (!Game1.currentLocation.IsOutdoors)
			{
				var layer = Game1.currentLocation.Map.GetLayer("Buildings");
				var xLimit = Game1.player.getTileX() + range;
				var yLimit = Game1.player.getTileY() + range;
				for (var x = Game1.player.getTileX() - range; x < xLimit && cookingStationLevel == 0; ++x)
				for (var y = Game1.player.getTileY() - range; y < yLimit && cookingStationLevel == 0; ++y)
				{
					var tile = layer.Tiles[x, y];
					if (tile == null
					    || Game1.currentLocation.doesTileHaveProperty(x, y, "Action", "Buildings") != "kitchen" 
					    && !Config.IndoorsTileIndexesThatActAsCookingStations.Contains(tile.TileIndex))
						continue;
					switch (Game1.currentLocation)
					{
						case FarmHouse farmHouse:
							cookingStationLevel = (farmHouse.upgradeLevel < 2
								? Math.Min(2, SaveData.ClientCookingEquipmentLevel)
								: Math.Max(farmHouse.upgradeLevel, SaveData.ClientCookingEquipmentLevel));
							break;
						default:
							cookingStationLevel = Math.Max(2, SaveData.ClientCookingEquipmentLevel);
							break;
					}

					Log.W($"Cooking station: {Game1.currentLocation.Name}: Kitchen (level {cookingStationLevel})");
				}
			}
			else
			{
				var xLimit = Game1.player.getTileX() + range;
				var yLimit = Game1.player.getTileY() + range;
				for (var x = Game1.player.getTileX() - range; x < xLimit && cookingStationLevel == 0; ++x)
				for (var y = Game1.player.getTileY() - range; y < yLimit && cookingStationLevel == 0; ++y)
				{
					Game1.currentLocation.Objects.TryGetValue(new Vector2(x, y), out var o);
					if (o == null || o.Name != "Campfire")
						continue;
					cookingStationLevel = SaveData.ClientCookingEquipmentLevel - 1;
					Log.W($"Cooking station: {cookingStationLevel}");
				}
			}
			Log.W("Cooking station search finished");
			return cookingStationLevel;
		}

		public static string UpdateEntry(string oldEntry, string[] newEntry, bool append = false, bool replace = false,
			int startIndex = 0, char delimiter = '/')
		{
			var fields = oldEntry.Split(delimiter);
			/*
			if (fields.Count == newEntry.Length)
				;
			else if (fields.Count < newEntry.Length)
				for (var i = fields.Count; i < newEntry.Length; ++i)
					fields.Add(null);
					*/
			if (replace)
				fields = newEntry;
			else for (var i = 0; i < newEntry.Length; ++i)
				if (newEntry[i] != null) 
					fields[startIndex + i] = append ? $"{fields[startIndex + i]} {newEntry[i]}" : newEntry[i];
			
			//var ne = newEntry.Aggregate((entry, field) => $"{entry}{delimiter}{field}").Remove(0, 0);
			var result = fields.Aggregate((entry, field) => $"{entry}{delimiter}{field}").Remove(0, 0);
			//Log.D($"Updated entry:\nvia: {ne} \nold: {oldEntry}\nnew: {result}", Config.DebugMode);
			return result;
		}
	}
}
