using System;

namespace PersistenceKit.Tests.Fixtures
{
    /// <summary>
    /// Hand-written stand-in for source-generator output. Mirrors the shape that the
    /// generator will emit:
    ///   - Properties with <c>MarkDirty</c> in the setter.
    ///   - Per-field resolved-target slots (<c>const</c> for explicit targets, <c>static</c>
    ///     for default-marked).
    ///   - <c>__ResolveDefaults</c> filling in the default slots and OR'ing into the mask.
    ///   - <c>WritePayload</c> / <c>ReadPayload</c> dispatching by target.
    /// </summary>
    public sealed class FixtureState : IPersistentState
    {
        public const string TYPE_ID = "FixtureState";

        // Backing fields. _name uses default target; _score is PlayerPrefs; _remote is Remote.
        private string _name;
        private int    _score;
        private string _remote;

        private string _slot = string.Empty;
        private Action<PersistTarget> _markDirty;

        // Per-field resolved-target slots. Default-marked fields use static (filled by
        // ResolveDefaults). Explicit-target fields use const.
        private static PersistTarget __t_name;
        private const  PersistTarget __t_score  = PersistTarget.PlayerPrefs;
        private const  PersistTarget __t_remote = PersistTarget.Remote;

        private static PersistTargetMask __mask = PersistTargetMask.PlayerPrefs | PersistTargetMask.Remote;

        public string Name   { get => _name;   set { _name = value;   _markDirty?.Invoke(__t_name); } }
        public int    Score  { get => _score;  set { _score = value;  _markDirty?.Invoke(__t_score); } }
        public string Remote { get => _remote; set { _remote = value; _markDirty?.Invoke(__t_remote); } }

        string IPersistentState.Key => _slot.Length == 0 ? TYPE_ID : (TYPE_ID + ":" + _slot);
        PersistTargetMask IPersistentState.TargetMask => __mask;

        void IPersistentState.WritePayload(PersistTarget target, IPayloadWriter w)
        {
            if (target == __t_name)   w.WriteString("Name",   _name ?? string.Empty, false);
            if (target == __t_score)  w.WriteInt32 ("Score",  _score,                false);
            if (target == __t_remote) w.WriteString("Remote", _remote ?? string.Empty, false);
        }

        void IPersistentState.ReadPayload(PersistTarget target, IPayloadReader r)
        {
            if (target == __t_name   && r.ReadString("Name",   false, out var n)) _name   = n;
            if (target == __t_score  && r.ReadInt32 ("Score",  false, out var s)) _score  = s;
            if (target == __t_remote && r.ReadString("Remote", false, out var rv)) _remote = rv;
        }

        void IPersistentState.Bind(string slot, Action<PersistTarget> markDirty)
        {
            _slot      = slot ?? string.Empty;
            _markDirty = markDirty;
        }

        public void MarkDirty()
        {
            var mask = (byte)__mask;
            if ((mask & 1) != 0) _markDirty?.Invoke(PersistTarget.Json);
            if ((mask & 2) != 0) _markDirty?.Invoke(PersistTarget.Binary);
            if ((mask & 4) != 0) _markDirty?.Invoke(PersistTarget.PlayerPrefs);
            if ((mask & 8) != 0) _markDirty?.Invoke(PersistTarget.Remote);
        }
        public void MarkDirty(PersistTarget target) => _markDirty?.Invoke(target);

        // Mirrors what the generator emits. Tests register this manually.
        public static void ResolveDefaults(PersistTarget defaultTarget)
        {
            __t_name = defaultTarget;
            __mask   = (PersistTargetMask)((byte)__mask | (byte)(1 << (int)defaultTarget));
        }

        // Test-only: lets per-test setup roll the static state back to its original shape.
        public static void __TestOnlyResetStatics()
        {
            __t_name = default;
            __mask   = PersistTargetMask.PlayerPrefs | PersistTargetMask.Remote;
        }
    }

    /// <summary>Second fixture type with a single-target footprint, used for slot tests.</summary>
    public sealed class SoloState : IPersistentState
    {
        public const string TYPE_ID = "SoloState";

        private string _value;
        private string _slot = string.Empty;
        private Action<PersistTarget> _markDirty;
        private static PersistTarget __t_value;
        private static PersistTargetMask __mask;

        public string Value { get => _value; set { _value = value; _markDirty?.Invoke(__t_value); } }

        string IPersistentState.Key => _slot.Length == 0 ? TYPE_ID : (TYPE_ID + ":" + _slot);
        PersistTargetMask IPersistentState.TargetMask => __mask;

        void IPersistentState.WritePayload(PersistTarget target, IPayloadWriter w)
        {
            if (target == __t_value) w.WriteString("Value", _value ?? string.Empty, false);
        }
        void IPersistentState.ReadPayload(PersistTarget target, IPayloadReader r)
        {
            if (target == __t_value && r.ReadString("Value", false, out var v)) _value = v;
        }
        void IPersistentState.Bind(string slot, Action<PersistTarget> markDirty)
        {
            _slot = slot ?? string.Empty;
            _markDirty = markDirty;
        }

        public void MarkDirty()
        {
            var mask = (byte)__mask;
            if ((mask & 1) != 0) _markDirty?.Invoke(PersistTarget.Json);
            if ((mask & 2) != 0) _markDirty?.Invoke(PersistTarget.Binary);
            if ((mask & 4) != 0) _markDirty?.Invoke(PersistTarget.PlayerPrefs);
            if ((mask & 8) != 0) _markDirty?.Invoke(PersistTarget.Remote);
        }
        public void MarkDirty(PersistTarget target) => _markDirty?.Invoke(target);

        public static void ResolveDefaults(PersistTarget defaultTarget)
        {
            __t_value = defaultTarget;
            __mask    = (PersistTargetMask)(1 << (int)defaultTarget);
        }

        public static void __TestOnlyResetStatics()
        {
            __t_value = default;
            __mask    = PersistTargetMask.None;
        }
    }
}
