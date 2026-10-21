using System;
using System.Collections.Generic;
using SummerCoop.Core;

internal static class VehicleMotionTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static Q4 Yaw(double radians) { return new Q4(0, (float)Math.Sin(radians / 2), 0, (float)Math.Cos(radians / 2)); }
    static VehicleMotionPose Sample(VehicleMotionBuffer buffer, float now)
    {
        VehicleMotionPose pose; Check(buffer.TrySample(now, out pose), "Missing presentation pose"); return pose;
    }
    sealed class Packet { public float Time, Arrival; }
    public static void Run(Action<string, Action> test)
    {
        test("Satsuma history stays smooth at 30/60/144 FPS through jitter, loss, duplicates and reordered snapshots", delegate {
            foreach (int fps in new[] { 30, 60, 144 })
            {
                var buffer = new VehicleMotionBuffer(); var packets = new List<Packet>();
                float[] jitter = { 0, .08f, .015f, .06f, .002f, .04f };
                for (int i = 0; i <= 240; i++)
                    if (i % 19 != 9) packets.Add(new Packet { Time = i * .05f, Arrival = 50 + i * .05f + jitter[i % jitter.Length] });
                packets.Sort(delegate(Packet a, Packet b) { return a.Arrival.CompareTo(b.Arrival); });
                int next = 0; float previous = -1, clock = -1;
                for (int frame = 0; frame < fps * 12; frame++)
                {
                    float now = 50 + (float)frame / fps;
                    while (next < packets.Count && packets[next].Arrival <= now)
                    {
                        Packet packet = packets[next++];
                        buffer.Add(packet.Time, packet.Arrival, new V3(packet.Time * 20, 2.5f, 0), Yaw(packet.Time * .3), new V3(20, 0, 0), new V3(0, .3f, 0));
                        // A duplicate/old arrival must not move the clock or the car.
                        Check(!buffer.Add(packet.Time, packet.Arrival + .001f, new V3(-100, 50, 0), Q4.Identity, new V3(), new V3()), "Duplicate accepted");
                    }
                    if (buffer.Count == 0) continue;
                    VehicleMotionPose pose = Sample(buffer, now);
                    Check(pose.Position.IsFinite && pose.Rotation.IsFinite && Math.Abs(pose.Position.Y - 2.5f) < .0001f, "Artificial vertical jump");
                    Check(pose.Position.X + .0001f >= previous && buffer.PlaybackTime >= clock, "Playback reversed after jitter");
                    Check(Math.Abs(pose.Position.X - pose.Time * 20) < .001f, "Constant-speed path changed");
                    if (previous >= 0) Check(pose.Position.X - previous <= 20f / fps + .045f,
                        "Delivery jitter became a position jump: fps=" + fps + ", now=" + now + ", step=" + (pose.Position.X - previous) + ", time=" + pose.Time);
                    previous = pose.Position.X; clock = buffer.PlaybackTime;
                    Check(buffer.Count <= 32, "History grew without bound");
                }
                Check(previous > 230, "Car stopped receiving motion");
            }
        });
        test("buffered acceleration, braking and turns match the delayed source trajectory", delegate {
            foreach (int fps in new[] { 30, 60, 144 })
            {
                var buffer = new VehicleMotionBuffer(); int next = 0;
                for (int frame = 0; frame < fps * 3; frame++)
                {
                    float now = (float)frame / fps;
                    while (next * .05f <= now)
                    {
                        float t = next++ * .05f, moving = Math.Min(t, 2);
                        float x = (float)(10 * (moving - Math.Sin(Math.PI * moving) / Math.PI));
                        float speed = (float)(10 * (1 - Math.Cos(Math.PI * moving)));
                        buffer.Add(t, t, new V3(x, 0, 0), Yaw(moving * .7), new V3(speed, 0, 0), new V3(0, t < 2 ? .7f : 0, 0));
                    }
                    VehicleMotionPose pose = Sample(buffer, now); float time = Math.Min(pose.Time, 2);
                    double expected = 10 * (time - Math.Sin(Math.PI * time) / Math.PI);
                    Check(Math.Abs(pose.Position.X - expected) < .011, "Acceleration/braking deviated from the source");
                    Check(Math.Abs(Math.Abs(pose.Rotation.Y * Yaw(time * .7).Y + pose.Rotation.W * Yaw(time * .7).W) - 1) < .0001, "Turn interpolation drifted");
                }
                Check(Math.Abs(Sample(buffer, 3).Position.X - 20) < .001, "Stationary car kept drifting");
            }
        });
        test("prediction stops after 100 ms including angular motion and resumes on new source data", delegate {
            var buffer = new VehicleMotionBuffer();
            buffer.Add(0, 10, new V3(), Q4.Identity, new V3(2, 0, 0), new V3(0, (float)Math.PI / 2, 0));
            VehicleMotionPose early = Sample(buffer, 10.15f), limit = Sample(buffer, 10.5f);
            Check(Math.Abs(early.Position.X - .1f) < .0001 && Math.Abs(limit.Position.X - .2f) < .0001, "Prediction interval changed");
            VehicleMotionPose held = Sample(buffer, 10.6f);
            Check(limit.Position.Distance(held.Position) == 0 && limit.Rotation.Y == held.Rotation.Y, "Missing packets produced unbounded drift");
            Check(Math.Abs(limit.Rotation.Y - Yaw(Math.PI / 20).Y) < .0001, "Angular prediction is not in radians/world space");
            buffer.Add(.6f, 10.6f, new V3(1.2f, 0, 0), Q4.Identity, new V3(2, 0, 0), new V3());
            Check(Math.Abs(Sample(buffer, 10.6f).Position.X - .2f) < .0001, "New packet jumped the held pose");
            Check(Sample(buffer, 10.65f).Position.X > .2f, "Recovery replayed an obsolete frame");
        });
        test("recovery from a burst of missing snapshots never jumps to the live clock", delegate {
            foreach (int fps in new[] { 30, 60, 144 })
            {
                var buffer = new VehicleMotionBuffer(); int next = 0; float previous = 0;
                for (int frame = 0; frame <= fps * 3; frame++)
                {
                    float now = (float)frame / fps;
                    while (next * .05f <= now)
                    {
                        float t = next++ * .05f;
                        if (t > .1001f && t < .4999f) continue;
                        buffer.Add(t, t, new V3(t * 20, 0, 0), Q4.Identity, new V3(20, 0, 0), new V3());
                    }
                    VehicleMotionPose pose = Sample(buffer, now);
                    Check(pose.Position.X >= previous && pose.Position.X - previous < 22f / fps + .001f, "Burst recovery jumped or reversed");
                    previous = pose.Position.X;
                }
                Check(Math.Abs(previous - 58) < .01, "Recovery never returned to the normal presentation delay");
            }
        });
        test("rotation crosses 180 degrees by the short arc and invalid poses leave history intact", delegate {
            var buffer = new VehicleMotionBuffer();
            buffer.Add(1, 11, new V3(), Yaw(170 * Math.PI / 180), new V3(), new V3());
            buffer.Add(1.05f, 11.05f, new V3(), Yaw(-170 * Math.PI / 180), new V3(), new V3());
            VehicleMotionPose middle = Sample(buffer, 11.125f);
            Check(Math.Abs(middle.Rotation.Y) > .9999 && Math.Abs(middle.Rotation.W) < .001, "Rotation took the long arc");
            Check(!buffer.Add(1.1f, 11.1f, new V3(float.NaN, 0, 0), Q4.Identity, new V3(), new V3()), "Nonfinite position accepted");
            Check(!buffer.Add(1.1f, 11.1f, new V3(), new Q4(), new V3(), new V3()), "Zero quaternion accepted");
            Check(!buffer.Add(1.1f, 11.1f, new V3(), new Q4(1e30f, 0, 0, 1), new V3(), new V3()), "Overflowing quaternion accepted");
            Check(buffer.Count == 2, "Invalid data mutated history");
        });
        test("a snapshot refining prediction preserves the current pose and bounds the correction", delegate {
            var buffer = new VehicleMotionBuffer();
            buffer.Add(0, 0, new V3(), Q4.Identity, new V3(3, 0, 0), new V3(0, 1, 0));
            VehicleMotionPose predicted = Sample(buffer, .2f);
            buffer.Add(.2f, .2f, new V3(.2f, 0, 0), Yaw(.02), new V3(1, 0, 0), new V3(0, .1f, 0));
            VehicleMotionPose refined = Sample(buffer, .2f);
            Check(refined.Position.Distance(predicted.Position) < .00001f
                && Math.Abs(refined.Rotation.Y - predicted.Rotation.Y) < .00001f, "Receipt jumped a predicted pose");
            VehicleMotionPose next = Sample(buffer, .2166667f);
            Check(next.Position.Distance(refined.Position) < .03f, "Prediction correction made an abrupt step");
            Check(next.Position.X >= refined.Position.X, "Refinement pulled an advancing car backwards");
            VehicleMotionPose capped = Sample(buffer, .6f), held = Sample(buffer, 1);
            Check(capped.Position.Distance(held.Position) == 0 && capped.Rotation.Y == held.Rotation.Y, "Correction kept drifting after prediction stopped");
            buffer.Add(1.1f, 1.1f, new V3(-1, 0, 0), Q4.Identity, new V3(-3, 0, 0), new V3());
            Check(Sample(buffer, 1.15f).Position.X < held.Position.X, "Correction limiter blocked a real reverse");
        });
        test("handoff clears the previous clock and teleports or legacy samples never blend unrelated histories", delegate {
            var buffer = new VehicleMotionBuffer(); VehicleMotionPose empty;
            Check(!buffer.TrySample(0, out empty), "Empty buffer supplied an origin pose");
            buffer.Add(100, 200, new V3(1, 0, 0), Q4.Identity, new V3(), new V3()); Sample(buffer, 201);
            buffer.Clear(); buffer.Add(0, 201, new V3(2, 0, 0), Q4.Identity, new V3(), new V3());
            Check(Sample(buffer, 201).Position.X == 2 && buffer.PlaybackTime < 0, "Handoff retained the old owner's playback clock");
            buffer.Add(.05f, 201.05f, new V3(60, 0, 0), Q4.Identity, new V3(), new V3());
            Check(buffer.Count == 1 && Sample(buffer, 201.06f).Position.X == 60, "Teleport was interpolated as a fast drive");
            buffer.Clear(); buffer.Add(-1, 3, new V3(3, 0, 0), Q4.Identity, new V3(1, 0, 0), new V3());
            buffer.Add(-1, 3.05f, new V3(3.05f, 0, 0), Q4.Identity, new V3(1, 0, 0), new V3());
            Check(Math.Abs(Sample(buffer, 3.125f).Position.X - 3.025f) < .0001, "Legacy timing was not based on receipt");
        });
    }
}
