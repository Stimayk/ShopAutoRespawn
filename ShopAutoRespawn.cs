using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using ShopAPI;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace ShopAutoRespawn
{
    public class ShopAutoRespawn : BasePlugin
    {
        public override string ModuleName => "[SHOP] Auto Respawn";
        public override string ModuleDescription => "";
        public override string ModuleAuthor => "E!N";
        public override string ModuleVersion => "1.0.1";

        private IShopApi? SHOP_API;
        private const string CategoryName = "AutoRespawn";
        public static JObject? JsonAutoRespawn { get; private set; }
        private readonly PlayerAutoRespawn[] playerAutoRespawns = new PlayerAutoRespawn[65];
        private Timer? _generalTimer;
        private int _seconds;

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            SHOP_API = IShopApi.Capability.Get();
            if (SHOP_API == null) return;

            LoadConfig();
            InitializeShopItems();
            SetupTimersAndListeners();
        }

        private void LoadConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "../../configs/plugins/Shop/AutoRespawn.json");
            if (File.Exists(configPath))
            {
                JsonAutoRespawn = JObject.Parse(File.ReadAllText(configPath));
            }
        }

        private void InitializeShopItems()
        {
            if (JsonAutoRespawn == null || SHOP_API == null) return;

            SHOP_API.CreateCategory(CategoryName, "Авто респавн");

            foreach (var item in JsonAutoRespawn.Properties().Where(p => p.Value is JObject))
            {
                Task.Run(async () =>
                {
                    int itemId = await SHOP_API.AddItem(
                        item.Name,
                        (string)item.Value["name"]!,
                        CategoryName,
                        (int)item.Value["price"]!,
                        (int)item.Value["sellprice"]!,
                        (int)item.Value["duration"]!
                    );
                    SHOP_API.SetItemCallbacks(itemId, OnClientBuyItem, OnClientSellItem, OnClientToggleItem);
                }).Wait();
            }
        }

        private void SetupTimersAndListeners()
        {
            RegisterListener<Listeners.OnClientDisconnect>(playerSlot =>
            {
                playerAutoRespawns[playerSlot] = null!;
                if (playerAutoRespawns[playerSlot]?.RespawnTimer != null)
                {
                    playerAutoRespawns[playerSlot].RespawnTimer?.Kill();
                }
            });
            RegisterListener<Listeners.OnClientConnected>(slot =>
            {
                playerAutoRespawns[slot] = new PlayerAutoRespawn(new Config(), 0, null);
            });

            RegisterEventHandler<EventRoundStart>((@event, info) =>
            {
                for (var i = 0; i < playerAutoRespawns.Length; i++)
                {
                    if (playerAutoRespawns[i] != null)
                    {
                        playerAutoRespawns[i].UsedRespawns = 0;
                        playerAutoRespawns[i].RespawnTimer?.Kill();
                    }
                }

                _generalTimer?.Kill();

                _generalTimer = AddTimer(1.0f, GeneralTimer, TimerFlags.REPEAT);
                _seconds = 0;

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                var player = @event.Userid;

                if (player == null || !player.IsValid ||
                    player.TeamNum is (int)CsTeam.None or (int)CsTeam.Spectator ||
                    playerAutoRespawns[player.Slot] == null)
                    return HookResult.Continue;

                playerAutoRespawns[player.Slot] = playerAutoRespawns[player.Slot] with
                {
                    RespawnTimer = AddTimer(playerAutoRespawns[player.Slot].Config.Delay,
                        () => RespawnPlayer(player))
                };

                return HookResult.Continue;
            });
        }

        public HookResult OnClientBuyItem(CCSPlayerController player, int itemId, string categoryName, string uniqueName,
            int buyPrice, int sellPrice, int duration, int count)
        {
            if (TryGetConfig(uniqueName, out var config))
            {
                playerAutoRespawns[player.Slot] = playerAutoRespawns[player.Slot] with
                {
                    Config = config,
                    ItemId = itemId
                };
            }
            else
            {
                Logger.LogError($"{uniqueName} has invalid or missing 'config' in config!");
            }

            return HookResult.Continue;
        }

        public HookResult OnClientToggleItem(CCSPlayerController player, int itemId, string uniqueName, int state)
        {
            if (state == 1 && TryGetConfig(uniqueName, out var config))
            {
                playerAutoRespawns[player.Slot] = playerAutoRespawns[player.Slot] with
                {
                    Config = config,
                    ItemId = itemId
                };
            }
            else if (state == 0)
            {
                OnClientSellItem(player, itemId, uniqueName, 0);
            }

            return HookResult.Continue;
        }

        public HookResult OnClientSellItem(CCSPlayerController player, int itemId, string uniqueName, int sellPrice)
        {
            playerAutoRespawns[player.Slot] = playerAutoRespawns[player.Slot] with
            {
                Config = new Config(),
                ItemId = 0
            };

            return HookResult.Continue;
        }

        private void GeneralTimer()
        {
            _seconds++;
        }

        private void RespawnPlayer(CCSPlayerController player)
        {
            var playerPawn = player.PlayerPawn.Value;
            var playerAutoRespawn = playerAutoRespawns[player.Slot];

            if (playerPawn == null || player.PawnIsAlive ||
                player.TeamNum is (int)CsTeam.None or (int)CsTeam.Spectator ||
                _seconds >= playerAutoRespawn.Config.RoundTime ||
                playerAutoRespawn.UsedRespawns >= playerAutoRespawn.Config.Amount)
                return;

            player.Respawn();

            playerPawn.Health = playerAutoRespawn.Config.HP;
            Utilities.SetStateChanged(playerPawn, "CBaseEntity", "m_iHealth");
            playerAutoRespawn.UsedRespawns++;
        }

        private static bool TryGetConfig(string uniqueName, out Config config)
        {
            config = new Config();
            if (JsonAutoRespawn != null && JsonAutoRespawn.TryGetValue(uniqueName, out var obj) && obj is JObject jsonItem &&
                jsonItem["hp"] != null && jsonItem["amount"] != null && jsonItem["delay"] != null && jsonItem["roundtime"] != null)
            {
                config.HP = (int)jsonItem["hp"]!;
                config.Amount = (int)jsonItem["amount"]!;
                config.Delay = (float)jsonItem["delay"]!;
                config.RoundTime = (float)jsonItem["roundtime"]!;
                return true;
            }
            return false;
        }

        public record class PlayerAutoRespawn(Config Config, int ItemId, Timer? RespawnTimer)
        {
            public int UsedRespawns { get; set; }
        };
    }

    public class Config
    {
        public int HP { get; set; } = 1;
        public int Amount { get; set; } = 1;
        public float Delay { get; set; } = 3.0f;
        public float RoundTime { get; set; } = 30.0f;
    }
}