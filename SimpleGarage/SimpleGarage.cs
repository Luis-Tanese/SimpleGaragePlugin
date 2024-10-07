using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Cysharp.Threading.Tasks;
using OpenMod.Unturned.Plugins;
using OpenMod.API.Plugins;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using UnityEngine;
using OpenMod.Core.Commands;
using OpenMod.API.Commands;
using OpenMod.API.Persistence;
using Steamworks;

[assembly: PluginMetadata("Tanese.SimpleGarage", DisplayName = "Simple Garage")]

namespace SimpleGarageSpace
{
    public class SimpleGarage : OpenModUnturnedPlugin
    {
        private readonly IConfiguration m_Configuration;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<SimpleGarage> m_Logger;
        private readonly IDataStore m_DataStore;

        public SimpleGarage(
            IConfiguration configuration,
            IStringLocalizer stringLocalizer,
            ILogger<SimpleGarage> logger,
            IServiceProvider serviceProvider,
            IDataStore dataStore) : base(serviceProvider)
        {
            m_Configuration = configuration;
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
            m_DataStore = dataStore;
        }

        protected override async UniTask OnLoadAsync()
        {
            await UniTask.SwitchToMainThread();
            m_Logger.LogInformation("Simple Garage by Tanese Loaded!");
        }

        protected override async UniTask OnUnloadAsync()
        {
            await UniTask.SwitchToMainThread();
            m_Logger.LogInformation("Simple Garage by Tanese Unloaded!");
        }

        public class GlobalGarageData
        {
            public Dictionary<string, PlayerGarageData> PlayerGarages { get; set; } = new Dictionary<string, PlayerGarageData>(); // Store each player's data
        }

        public class PlayerGarageData
        {
            public List<VehicleData> Vehicles { get; set; } = new List<VehicleData>();
        }

        public class SimpleItemData
        {
            public ushort Id { get; set; }
            public byte Durability { get; set; }
            public byte[]? Metadata { get; set; } = Array.Empty<byte>();
        }

        public class VehicleData
        {
            public uint GarageVehicleId { get; set; }
            public uint VehicleId { get; set; }
            public string VehicleName { get; set; } = string.Empty;
            public ushort Health { get; set; }
            public ushort Fuel { get; set; }
            public List<SimpleItemData> Inventory { get; set; } = new List<SimpleItemData>();
        }

        private InteractableVehicle? GetVehicleSillyLookingAt(Player player)
        {
            if (Physics.Raycast(player.look.aim.position, player.look.aim.forward, out RaycastHit hit, 4, RayMasks.VEHICLE) && hit.transform.TryGetComponent(out InteractableVehicle vehicle))
            {
                Debug.Log(vehicle?.asset.vehicleName);
                return vehicle;
            }
            Debug.Log("No vehicle detected");
            return null;
        }

        [Command("garageadd")]
        [CommandAlias("gadd")]
        [CommandDescription("Adds the vehicle you are looking at to your garage")]
        public class CommandGarageAdd : OpenMod.Core.Commands.Command
        {
            private readonly SimpleGarage m_Plugin;
            private readonly IUnturnedUserDirectory m_userDirectory;
            public static uint m_nextGarageId = 1;

            public CommandGarageAdd(
                IUnturnedUserDirectory userDirectory,
                SimpleGarage plugin,
                IServiceProvider serviceProvider) : base(serviceProvider)
            {
                m_Plugin = plugin;
                m_userDirectory = userDirectory;
            }

            protected override async Task OnExecuteAsync()
            {
                UnturnedUser user = (UnturnedUser)Context.Actor;
                var player = user.Player.Player;
                var vehicle = m_Plugin.GetVehicleSillyLookingAt(player) ?? throw new UserFriendlyException("No vehicle found.");

                if (!vehicle.isLocked)
                {
                    throw new UserFriendlyException("You need to lock the vehicle before adding it to the garage.");
                }

                if (vehicle.lockedOwner != user.SteamId)
                {
                    throw new UserFriendlyException("You can't add someone else's vehicle to your garage.");
                }

                string globalKey = "SimpleGarageSpace.garage";
                var globalGarageData = await m_Plugin.m_DataStore.LoadAsync<GlobalGarageData>(globalKey) ?? new GlobalGarageData();
                string playerId = user.SteamId.ToString();

                if (!globalGarageData.PlayerGarages.TryGetValue(playerId, out var playerGarageData))
                {
                    playerGarageData = new PlayerGarageData();
                    globalGarageData.PlayerGarages[playerId] = playerGarageData;
                }

                List<SimpleItemData> simpleInventoryItems = new List<SimpleItemData>();
                foreach (var itemJar in vehicle.trunkItems.items)
                {
                    var item = itemJar.item;
                    var simpleItemData = new SimpleItemData
                    {
                        Id = item.id,
                        Durability = item.durability,
                        Metadata = item.metadata 
                    };

                    simpleInventoryItems.Add(simpleItemData);
                }

                var vehicleData = new VehicleData
                {
                    GarageVehicleId = m_nextGarageId++,
                    VehicleId = vehicle.id,
                    VehicleName = vehicle.asset.vehicleName,
                    Health = vehicle.health,
                    Fuel = vehicle.fuel,
                    Inventory = simpleInventoryItems
                };

                playerGarageData.Vehicles.Add(vehicleData);
                await m_Plugin.m_DataStore.SaveAsync(globalKey, globalGarageData);
                await UniTask.SwitchToMainThread();
                vehicle.trunkItems.clear();
                VehicleManager.askVehicleDestroy(vehicle);
                await user.PrintMessageAsync($"Vehicle {vehicle.asset.vehicleName} has been added to your garage.");
            }
        }
        [Command("garagelist")]
        [CommandAlias("glist")]
        [CommandDescription("Lists all vehicles in your garage")]
        public class CommandGaragelist : OpenMod.Core.Commands.Command
        {
            private readonly SimpleGarage m_Plugin;
            private readonly IUnturnedUserDirectory m_userDirectory;

            public CommandGaragelist(
                IUnturnedUserDirectory userDirectory,
                SimpleGarage plugin,
                IServiceProvider serviceProvider) : base(serviceProvider)
            {
                m_userDirectory = userDirectory;
                m_Plugin = plugin;
            }

            protected override async Task OnExecuteAsync()
            {
                UnturnedUser user = (UnturnedUser)Context.Actor;
                string globalKey = "SimpleGarageSpace.garage";
                var globalGarageData = await m_Plugin.m_DataStore.LoadAsync<GlobalGarageData>(globalKey) ?? new GlobalGarageData();
                string playerId = user.SteamId.ToString();

                if (!globalGarageData.PlayerGarages.TryGetValue(playerId, out var playerGarageData))
                {
                    throw new UserFriendlyException("You don't have any vehicles in your garage.");
                }

                foreach (var vehicle in playerGarageData.Vehicles)
                {
                    await user.PrintMessageAsync($"ID: {vehicle.GarageVehicleId} - Vehicle: {vehicle.VehicleName}");
                }
            }
        }

        [Command("garageretrieve")]
        [CommandAlias("gret")]
        [CommandDescription("Retrieves a vehicle from your garage")]
        public class CommandGarageRetrieve : OpenMod.Core.Commands.Command 
        {
            private readonly SimpleGarage m_Plugin;
            private readonly IUnturnedUserDirectory m_userDirectory;

            public CommandGarageRetrieve(
                IUnturnedUserDirectory userDirectory,
                SimpleGarage plugin,
                IServiceProvider serviceProvider) : base(serviceProvider)
            {
                m_Plugin = plugin;
                m_userDirectory = userDirectory;
            }

            protected override async Task OnExecuteAsync()
            {
                UnturnedUser user = (UnturnedUser)Context.Actor;
                string globalKey = "SimpleGarageSpace.garage";
                var globalGarageData = await m_Plugin.m_DataStore.LoadAsync<GlobalGarageData>(globalKey) ?? new GlobalGarageData();
                string playerId = user.SteamId.ToString();

                if (!globalGarageData.PlayerGarages.TryGetValue(playerId, out var playerGarageData))
                {
                    throw new UserFriendlyException("You don't have any vehicles in your garage.");
                }

                if (Context.Parameters.Count < 1)
                {
                    throw new UserFriendlyException("You must provide the vehicle ID.");
                }

                var garageVehicleId = await Context.Parameters.GetAsync<int>(0);
                var vehicle = playerGarageData.Vehicles.Find(v => v.GarageVehicleId == garageVehicleId) ?? throw new UserFriendlyException("No vehicle with that ID was found in your garage.");
                playerGarageData.Vehicles.Remove(vehicle);
                await m_Plugin.m_DataStore.SaveAsync(globalKey, globalGarageData);
                await UniTask.SwitchToMainThread();
                var spawnPosition = user.Player.Player.transform.position + new Vector3(0, 5, 0);
                var spawnedVehicle = VehicleManager.spawnVehicleV2((ushort)vehicle.VehicleId, spawnPosition, Quaternion.identity);

                spawnedVehicle.askRepair(vehicle.Health);
                spawnedVehicle.askFillFuel(vehicle.Fuel);

                foreach (var itemData in vehicle.Inventory)
                {
                    var item = new Item(itemData.Id, 1, itemData.Durability);
                    
                    if (itemData.Metadata != null && itemData.Metadata.Length > 0)
                    {
                        item.metadata = itemData.Metadata;
                    }

                    spawnedVehicle.trunkItems.tryAddItem(item);
                }

                VehicleManager.ServerSetVehicleLock(spawnedVehicle, user.SteamId, CSteamID.Nil, true);
                playerGarageData.Vehicles.Remove(vehicle);
                await user.PrintMessageAsync($"Vehicle {vehicle.VehicleName} (ID {vehicle.GarageVehicleId}) has been retrieved from your garage.");
            }
        }

    }
}
