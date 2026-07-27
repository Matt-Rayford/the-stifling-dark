using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StiflingDark.Engine.Core;

namespace StiflingDark.Protocol
{
    /// <summary>
    /// JSON wire encoding for player commands and per-seat views, using an explicit
    /// reflection-built type registry with a "$type" discriminator (never TypeNameHandling —
    /// clients must not be able to name arbitrary types for the server to instantiate).
    /// </summary>
    public static class WireCodec
    {
        public static JsonSerializerSettings Settings { get; } = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            // A redacted field is ABSENT, not null: dropping nulls is what makes "hidden" and
            // "not there" indistinguishable in the bytes a client receives.
            NullValueHandling = NullValueHandling.Ignore,
            Converters =
            {
                // Enums travel as strings ("investigator", "adversarySetup") — readable, and
                // robust against enum members being reordered between client and server builds.
                new Newtonsoft.Json.Converters.StringEnumConverter
                {
                    NamingStrategy = new CamelCaseNamingStrategy(),
                },
            },
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        private static readonly Dictionary<string, Type> CommandTypes = BuildRegistry<GameCommand>();
        private static readonly Dictionary<Type, string> CommandNames =
            CommandTypes.ToDictionary(kv => kv.Value, kv => kv.Key);

        /// <summary>Every command name this build understands — handy for client self-checks.</summary>
        public static IReadOnlyCollection<string> KnownCommands => CommandTypes.Keys;

        private static Dictionary<string, Type> BuildRegistry<TBase>()
        {
            return typeof(TBase).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(TBase).IsAssignableFrom(t))
                .ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);
        }

        // ---------------------------------------------------------- commands

        public static JObject EncodeCommand(GameCommand command)
        {
            var json = JObject.FromObject(command, Serializer);
            json["$type"] = CommandNames[command.GetType()];
            return json;
        }

        public static GameCommand DecodeCommand(JObject json)
        {
            string name = (string?)json["$type"]
                ?? throw new JsonSerializationException("Command is missing $type.");
            if (!CommandTypes.TryGetValue(name, out var type))
            {
                throw new JsonSerializationException($"Unknown command type '{name}'.");
            }
            return (GameCommand?)json.ToObject(type, Serializer)
                ?? throw new JsonSerializationException($"Could not decode '{name}'.");
        }

        // ------------------------------------------------------------- views

        /// <summary>
        /// Serialize a per-seat view. There is deliberately no encoder that takes a
        /// <see cref="GameState"/>: the only way to put board state on the wire is to project
        /// it through <see cref="Game.ViewFor"/> first.
        /// </summary>
        public static JObject EncodeView(PlayerView view) => JObject.FromObject(view, Serializer);

        public static PlayerView? DecodeView(JObject json) =>
            json.ToObject<PlayerView>(Serializer);

        public static List<LogEntryMessage> EncodeLog(IEnumerable<PlayerView.LogEntry> entries) =>
            entries.Select(e => new LogEntryMessage
            {
                Round = e.Round,
                Type = e.Type,
                Detail = e.Detail,
            }).ToList();
    }
}
