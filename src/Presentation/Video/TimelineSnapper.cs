using System;
using System.Collections.Generic;
using System.Linq;
namespace QuickCapture;
internal sealed record TimelineSnapTarget(double Time, string Label, int Priority = 0);
internal sealed class TimelineSnapper
{
    internal TimelineSnapTarget? Target { get; private set; }
    internal void Reset() => Target = null;
    internal double Resolve(double time, double duration, double span, IEnumerable<TimelineSnapTarget> targets, double minimum, double maximum, bool enabled)
    {
        if (!enabled || duration <= 0) { Reset(); return time; }
        var valid = targets.Where(t => t.Time >= minimum - .000001 && t.Time <= maximum + .000001).ToArray();
        double Distance(double t) => Math.Abs(t - time) * span / duration;
        if (Target != null && valid.Contains(Target) && Distance(Target.Time) <= 12) return Target.Time;
        Target = valid.Where(t => Distance(t.Time) <= 8).OrderBy(t => Distance(t.Time)).ThenBy(t => t.Priority).ThenBy(t => t.Time).FirstOrDefault();
        return Target?.Time ?? time;
    }
}
