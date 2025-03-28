/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Orphaned Item Remover", "VisEntities", "1.0.0")]
    [Description(" ")]
    public class OrphanedItemRemover : RustPlugin
    {
        #region Fields

        private static OrphanedItemRemover _plugin;
        private static Configuration _config;

        private Timer _autoCleanupTimer;
        private bool _cleanupRunning;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Enable Auto Cleanup")]
            public bool EnableAutoCleanup { get; set; }

            [JsonProperty("Initial Delay Seconds")]
            public float InitialDelaySeconds { get; set; }

            [JsonProperty("Cleanup Interval Seconds")]
            public float CleanupIntervalSeconds { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                EnableAutoCleanup = false,
                InitialDelaySeconds = 5f,
                CleanupIntervalSeconds = 3600f
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
        }

        private void Unload()
        {
            if (_autoCleanupTimer != null)
                _autoCleanupTimer.Destroy();

            CoroutineUtil.StopAllCoroutines();

            _config = null;
            _plugin = null;
        }

        private void OnServerInitialized(bool isStartup)
        {
            if (_config.EnableAutoCleanup)
            {
                timer.Once(_config.InitialDelaySeconds, () =>
                {
                    StartOrphanedItemCleanup();
                    if (_config.CleanupIntervalSeconds > 0)
                    {
                        _autoCleanupTimer  = timer.Every(_config.CleanupIntervalSeconds, StartOrphanedItemCleanup);
                    }
                });
            }
        }

        #endregion Oxide Hooks

        #region Held Entity Cleanup

        private void StartOrphanedItemCleanup()
        {
            if (_cleanupRunning)
            {
                Puts("A cleanup pass is already running. Skipping new request...");
                return;
            }

            CoroutineUtil.StartCoroutine("CleanupOrphanedItemsCoroutine", CleanupOrphanedItemsCoroutine());
        }

        private IEnumerator CleanupOrphanedItemsCoroutine()
        {
            _cleanupRunning = true;

            var allEntities = BaseNetworkable.serverEntities;
            if (allEntities == null)
            {
                Puts("serverEntities list is null, aborting cleanup.");
                _cleanupRunning = false;
                yield break;
            }

            var stopwatch = Stopwatch.StartNew();
            var removedCount = 0;

            var allItems = new List<Item>();
            var allHeldEntities = new List<BaseEntity>();

            Puts($"Starting cleanup for {allEntities.Count} entities...");

            foreach (var entity in allEntities)
            {
                if (entity == null || !entity.IsValid())
                    continue;

                switch (entity)
                {
                    case HeldEntity heldEntity:
                        allHeldEntities.Add(heldEntity);
                        break;

                    case BasePlayer player:
                        if (player.inventory != null)
                            player.inventory.GetAllItems(allItems);
                        break;

                    case LootableCorpse corpse:
                        if (corpse.containers != null)
                        {
                            foreach (var cont in corpse.containers)
                            {
                                if (cont != null && cont.itemList != null)
                                    allItems.AddRange(cont.itemList);
                            }
                        }
                        break;

                    case StorageContainer storage:
                        if (storage.inventory != null && storage.inventory.itemList != null)
                            allItems.AddRange(storage.inventory.itemList);
                        break;

                    case ContainerIOEntity io:
                        if (io.inventory != null && io.inventory.itemList != null)
                            allItems.AddRange(io.inventory.itemList);
                        break;

                    case DroppedItem droppedItem:
                        if (droppedItem.item != null)
                            allItems.Add(droppedItem.item);
                        break;

                    case DroppedItemContainer droppedContainer:
                        if (droppedContainer.inventory != null && droppedContainer.inventory.itemList != null)
                            allItems.AddRange(droppedContainer.inventory.itemList);
                        break;
                }
            }

            Puts($"Collected items in {stopwatch.Elapsed.TotalMilliseconds:0.##} ms.");

            yield return CoroutineEx.waitForEndOfFrame;
            stopwatch.Restart();

            for (int i = allItems.Count - 1; i >= 0; i--)
            {
                var subList = allItems[i].contents?.itemList;
                if (subList != null)
                    allItems.AddRange(subList);
            }

            for (int i = allItems.Count - 1; i >= 0; i--)
            {
                var heldEnt = allItems[i].GetHeldEntity();
                if (heldEnt != null)
                    allHeldEntities.Remove(heldEnt);
            }

            Puts($"Processed sub-items in {stopwatch.Elapsed.TotalMilliseconds:0.##} ms.");

            yield return CoroutineEx.waitForEndOfFrame;
            stopwatch.Restart();

            var perItemWatch = Stopwatch.StartNew();

            for (int i = allHeldEntities.Count - 1; i >= 0; i--)
            {
                var entity = allHeldEntities[i];
                if (!entity.IsValid())
                    continue;

                if (entity.GetItem() != null && entity.GetItem().amount > 0)
                    continue;

                entity.Kill();
                removedCount++;

                if (perItemWatch.Elapsed.TotalMilliseconds >= 3.0)
                {
                    perItemWatch.Restart();
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            Puts($"Removed {removedCount} orphaned items in {stopwatch.Elapsed.TotalMilliseconds:0.##} ms. Total items considered: {allItems.Count}.");
            _cleanupRunning = false;
            yield break;
        }

        #endregion Held Entity Cleanup

        #region Helper Classes

        public static class CoroutineUtil
        {
            private static readonly Dictionary<string, Coroutine> _activeCoroutines = new Dictionary<string, Coroutine>();

            public static Coroutine StartCoroutine(string baseCoroutineName, IEnumerator coroutineFunction, string uniqueSuffix = null)
            {
                string coroutineName;

                if (uniqueSuffix != null)
                    coroutineName = baseCoroutineName + "_" + uniqueSuffix;
                else
                    coroutineName = baseCoroutineName;

                StopCoroutine(coroutineName);

                Coroutine coroutine = ServerMgr.Instance.StartCoroutine(coroutineFunction);
                _activeCoroutines[coroutineName] = coroutine;
                return coroutine;
            }

            public static void StopCoroutine(string baseCoroutineName, string uniqueSuffix = null)
            {
                string coroutineName;

                if (uniqueSuffix != null)
                    coroutineName = baseCoroutineName + "_" + uniqueSuffix;
                else
                    coroutineName = baseCoroutineName;

                if (_activeCoroutines.TryGetValue(coroutineName, out Coroutine coroutine))
                {
                    if (coroutine != null)
                        ServerMgr.Instance.StopCoroutine(coroutine);

                    _activeCoroutines.Remove(coroutineName);
                }
            }

            public static void StopAllCoroutines()
            {
                foreach (string coroutineName in _activeCoroutines.Keys.ToArray())
                {
                    StopCoroutine(coroutineName);
                }
            }
        }

        #endregion Helper Classes

        #region Commands

        [ConsoleCommand("orphanitem.remove")]
        private void cmdCleanupOrphans(ConsoleSystem.Arg conArgs)
        {
            if (conArgs == null)
                return;

            BasePlayer player = conArgs.Player();
            if (player != null)
                return;

            StartOrphanedItemCleanup();
            Puts("Started orphaned item cleanup routine...");
        }

        #endregion Commands
    }
}