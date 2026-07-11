using System;
using System.Collections.Generic;

namespace PersistenceKit.Tests.Fixtures
{
    /// <summary>Type-coverage fixture: one of every primitive plus a collection and an object.</summary>
    public sealed class RichFixtureState : IPersistentState
    {
        public const string TYPE_ID = "RichFixtureState";

        public string                Str;
        public bool                  Flag;
        public int                   I32;
        public long                  I64;
        public uint                  U32;
        public ulong                 U64;
        public float                 F32;
        public double                F64;
        public byte[]                Bytes;
        public MoodKind              Mood;
        public List<int>             List;
        public Dictionary<string, int> Dict;

        public enum MoodKind { Sad = 0, Neutral = 1, Happy = 2 }

        private string _slot = string.Empty;
        private Action<PersistTarget> _markDirty;

        // Single-target fixture — everything routes to Json.
        private static readonly PersistTargetMask __mask = PersistTargetMask.Json;

        string IPersistentState.Key => _slot.Length == 0 ? TYPE_ID : (TYPE_ID + ":" + _slot);
        PersistTargetMask IPersistentState.TargetMask => __mask;

        void IPersistentState.WritePayload(PersistTarget target, IPayloadWriter w)
        {
            if (target != PersistTarget.Json) return;
            w.WriteString("Str", Str ?? string.Empty, false);
            w.WriteBool   ("Flag", Flag, false);
            w.WriteInt32  ("I32", I32, false);
            w.WriteInt64  ("I64", I64, false);
            w.WriteUInt32 ("U32", U32, false);
            w.WriteUInt64 ("U64", U64, false);
            w.WriteSingle ("F32", F32, false);
            w.WriteDouble ("F64", F64, false);
            w.WriteBytes  ("Bytes", Bytes, false);
            w.WriteEnum   ("Mood", Mood, false);
            w.WriteObject ("List", List, typeof(List<int>), false);
            w.WriteObject ("Dict", Dict, typeof(Dictionary<string, int>), false);
        }

        void IPersistentState.ReadPayload(PersistTarget target, IPayloadReader r)
        {
            if (target != PersistTarget.Json) return;
            if (r.ReadString("Str", false, out var s)) Str = s;
            if (r.ReadBool  ("Flag", false, out var b)) Flag = b;
            if (r.ReadInt32 ("I32", false, out var i)) I32 = i;
            if (r.ReadInt64 ("I64", false, out var l)) I64 = l;
            if (r.ReadUInt32("U32", false, out var u32)) U32 = u32;
            if (r.ReadUInt64("U64", false, out var u64)) U64 = u64;
            if (r.ReadSingle("F32", false, out var f)) F32 = f;
            if (r.ReadDouble("F64", false, out var d)) F64 = d;
            if (r.ReadBytes ("Bytes", false, out var by)) Bytes = by;
            if (r.ReadEnum<MoodKind>("Mood", false, out var m)) Mood = m;
            if (r.ReadObject("List", typeof(List<int>), false, out var lo)) List = (List<int>)lo;
            if (r.ReadObject("Dict", typeof(Dictionary<string, int>), false, out var di)) Dict = (Dictionary<string, int>)di;
        }

        void IPersistentState.Bind(string slot, Action<PersistTarget> markDirty)
        {
            _slot = slot ?? string.Empty;
            _markDirty = markDirty;
        }

        public void MarkDirty() => _markDirty?.Invoke(PersistTarget.Json);
        public void MarkDirty(PersistTarget target) => _markDirty?.Invoke(target);
    }
}
