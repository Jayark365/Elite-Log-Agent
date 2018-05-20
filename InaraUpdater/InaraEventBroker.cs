﻿using InaraUpdater.Model;
using MoreLinq;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InaraUpdater
{
    public class InaraEventBroker : IObserver<JObject>
    {
        private readonly ApiFacade apiFacade;
        private readonly Interfaces.IPlayerStateHistoryRecorder playerStateRecorder;
        private readonly ILogger Log;
        private readonly List<ApiEvent> eventQueue = new List<ApiEvent>();

        private void Queue(ApiEvent e)
        {
            bool shouldFlush = false;
            lock (eventQueue)
            {
                if (e != null)
                    eventQueue.Add(e);
                shouldFlush = eventQueue.Count >= 1;
            }
            if (shouldFlush)
                Task.Factory.StartNew(FlushQueue);
        }

        private void FlushQueue()
        {
            List<ApiEvent> apiEvents;
            lock (eventQueue)
            {
                apiEvents = Compact(eventQueue)
                   .Where(e => e.Timestamp > DateTime.UtcNow.AddDays(-30)) // INARA API only accepts events for last month
                   //.Where(e => e.EventName == "addCommanderTravelDock" || e.EventName == "addCommanderTravelFSDJump") // DEBUG
                   .ToList();
                eventQueue.Clear();
            }
            if (apiEvents.Count > 0)
                apiFacade.ApiCall(apiEvents).Wait();
        }

        private static readonly string[] compactableEvents = new[] {
            "setCommanderInventoryMaterials",
            "setCommanderGameStatistics"
        };

        public InaraEventBroker(ApiFacade apiFacade, Interfaces.IPlayerStateHistoryRecorder playerStateRecorder, ILogger logger)
        {
            this.apiFacade = apiFacade ?? throw new ArgumentNullException(nameof(apiFacade));
            this.playerStateRecorder = playerStateRecorder ?? throw new ArgumentNullException(nameof(playerStateRecorder));
            Log = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void OnCompleted()
        {
            FlushQueue();
        }

        private static IEnumerable<ApiEvent> Compact(IEnumerable<ApiEvent> events)
        {
            var eventsByType = events
                .GroupBy(e => e.EventName, e => e)
                .ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var type in compactableEvents.Intersect(eventsByType.Keys))
                eventsByType[type] = new[] { eventsByType[type].MaxBy(e => e.Timestamp) };

            return eventsByType.Values.SelectMany(ev => ev).OrderBy(e => e.Timestamp);
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(JObject @event)
        {
            try
            {
                var eventName = @event["event"].ToString();
                switch (eventName)
                {
                    // Generic
                    case "LoadGame": Queue(ToCommanderCreditsEvent(@event)); break;
                    case "Materials": Queue(ToMaterialsInventoryEvent(@event)); break;
                    case "Statistics": Queue(ToStatisticsEvent(@event)); break;

                    // Travel
                    case "FSDJump": Queue(ToFsdJumpEvent(@event)); break;
                    case "Docked": Queue(ToDockedEvent(@event)); break;

                    // Engineers
                    case "EngineerProgress": Queue(ToEngineerProgressEvent(@event)); break;

                    // Combat
                    case "Interdicted":
                    case "Interdiction":
                    case "EscapeInterdiction":
                        Queue(ToInterdictionEvent(@event)); break;
                    //case "PVPKill":
                        //Queue(ToPvpKillEvent(@event)); break; 
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Error in OnNext");
            }
        }

        private ApiEvent ToPvpKillEvent(JObject @event)
        {
            // TODO: where do I take star system from?
            return new ApiEvent("addCommanderCombatKill")
            {
                EventData = new Dictionary<string, object> {
                    { "starsystemName", @event["StarSystem"].ToString() },
                    { "opponentName", @event["Victim"].ToString() },
                },
                Timestamp = DateTime.Parse(@event["timestamp"].ToString())
            };
        }

        private ApiEvent ToInterdictionEvent(JObject @event)
        {
            string eventType;
            switch (@event["event"].ToString())
            {
                case "Interdicted": eventType = "addCommanderCombatInterdicted"; break;
                case "Interdiction": eventType = "addCommanderCombatInterdiction"; break;
                case "EscapeInterdiction": eventType = "addCommanderCombatInterdictionEscape"; break;
                default: throw new ArgumentOutOfRangeException(nameof(@eventType));
            }

            if (@event["StarSystem"] == null)
                return null;

            return new ApiEvent(eventType)
            {
                EventData = new Dictionary<string, object> {
                    { "starsystemName", @event["StarSystem"]?.ToString() },
                    { "opponentName", (@event["Interdicted"] ?? @event["Interdictor"] ?? "Unknown").ToString() },
                    { "isPlayer", @event["IsPlayer"]?.ToObject<long>() },
                    { "isSuccess", @event["Success"]?.ToObject<bool>() }
                },
                Timestamp = DateTime.Parse(@event["timestamp"].ToString())
            };
        }

        private ApiEvent ToStatisticsEvent(JObject @event)
        {
            return new ApiEvent("setCommanderGameStatistics")
            {
                EventData = @event.ToObject<Dictionary<string, object>>(),
                Timestamp = DateTime.Parse(@event["timestamp"].ToString())
            };
        }

        private ApiEvent ToDockedEvent(JObject @event)
        {
            var timestamp = DateTime.Parse(@event["timestamp"].ToString());
            return new ApiEvent("addCommanderTravelDock")
            {
                EventData = new Dictionary<string, object> {
                    { "starsystemName", @event["StarSystem"].ToString() },
                    { "stationName", @event["StationName"].ToString()},
                    { "marketID", @event["MarketID"]?.ToObject<long>() },
                    { "shipGameID", playerStateRecorder.GetPlayerShipId(timestamp) },
                    { "shipType", playerStateRecorder.GetPlayerShipType(timestamp) }
                },
                Timestamp = timestamp
            };
        }

        private ApiEvent ToFsdJumpEvent(JObject @event)
        {
            var timestamp = DateTime.Parse(@event["timestamp"].ToString());
            return new ApiEvent("addCommanderTravelFSDJump")
            {
                EventData = new Dictionary<string, object> {
                    { "starsystemName", @event["StarSystem"].ToString() },
                    { "jumpDistance", @event["JumpDist"].ToObject<double>() },
                    { "shipGameID", playerStateRecorder.GetPlayerShipId(timestamp) },
                    { "shipType", playerStateRecorder.GetPlayerShipType(timestamp) }
                },
                Timestamp = timestamp
            };
        }

        private ApiEvent ToMaterialsInventoryEvent(JObject @event)
        {
            var materialCounts = @event["Raw"]
                .ToDictionary(
                    arrayItem => arrayItem["Name"].ToString(),
                    arrayItem => (object)Int32.Parse(arrayItem["Count"].ToString())
                );

            return new ApiEvent("setCommanderInventoryMaterials")
            {
                EventData = materialCounts.Select(kvp => new { itemName = kvp.Key, itemCount = kvp.Value }).ToArray(),
                Timestamp = DateTime.Parse(@event["timestamp"].ToString())
            };
        }

        private ApiEvent ToCommanderCreditsEvent(JObject @event)
        {
            return new ApiEvent("setCommanderCredits")
            {
                EventData = new Dictionary<string, object> {
                    { "commanderCredits", @event["Credits"]?.ToObject<long>() },
                    { "commanderLoan", @event["Loan"]?.ToObject<long>() }
                },
                Timestamp = DateTime.Parse(@event["timestamp"].ToString())
            };
        }

        private ApiEvent ToEngineerProgressEvent(JObject @event)
        {
            return new ApiEvent("setCommanderRankEngineer")
            {
                EventData = new Dictionary<string, object> {
                    { "engineerName", @event["Engineer"].ToString() },
                    { "rankStage", @event["Progress"]?.ToString() },
                    { "rankValue", @event["Rank"]?.ToObject<int>() }
                },
                Timestamp = DateTime.Parse(@event["timestamp"].ToString())
            };
        }
    }
}