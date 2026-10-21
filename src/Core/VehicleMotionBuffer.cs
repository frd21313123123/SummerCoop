using System;

namespace SummerCoop.Core
{
    public struct VehicleMotionPose
    {
        public float Time;
        public V3 Position, Velocity, AngularVelocity;
        public Q4 Rotation;
    }

    // Presentation history only. Ownership and packet ordering remain with the
    // transport receiver. No Unity calls: the same sampler runs in regressions.
    public sealed class VehicleMotionBuffer
    {
        public const float Delay = .1f, MaxPrediction = .1f;
        readonly VehicleMotionPose[] samples = new VehicleMotionPose[32];
        int start, count;
        bool timestamped, clockSet, played;
        float clockOffset, playback, playedAt;
        bool hasOutput;
        int revision, outputRevision;
        VehicleMotionPose output;
        float outputCurveTime;
        public int Count { get { return count; } }
        public float PlaybackTime { get { return playback; } }

        public void Clear()
        {
            start = count = 0; clockSet = played = false; playback = 0;
            hasOutput = false; revision = outputRevision = 0;
        }

        VehicleMotionPose At(int index) { return samples[(start + index) % samples.Length]; }

        public bool Add(float timestamp, float receivedAt, V3 position, Q4 rotation, V3 velocity, V3 angularVelocity)
        {
            if (!V3.Finite(timestamp) || !V3.Finite(receivedAt) || !position.IsFinite || !rotation.IsFinite
                || !velocity.IsFinite || !angularVelocity.IsFinite || !V3.Finite(Dot(rotation, rotation))
                || Dot(rotation, rotation) < .000001f) return false;
            bool hasTime = timestamp >= 0f;
            if (clockSet && timestamped != hasTime) Clear();
            float time = hasTime ? timestamp : receivedAt;
            if (count > 0 && time <= At(count - 1).Time) return false;
            if (count > 0)
            {
                VehicleMotionPose previous = At(count - 1);
                if (position.Distance(Add(previous.Position, Scale(previous.Velocity, time - previous.Time))) > 5f)
                    Clear(); // A real relocation must not be interpolated as a very fast drive.
            }
            float offset = hasTime ? receivedAt - timestamp : 0f;
            if (!clockSet || offset < clockOffset) clockOffset = offset;
            else clockOffset += (offset - clockOffset) * .02f;
            clockSet = true; timestamped = hasTime;
            if (count == samples.Length) { start = (start + 1) % samples.Length; count--; }
            samples[(start + count++) % samples.Length] = new VehicleMotionPose {
                Time = time, Position = position, Rotation = Normalize(rotation), Velocity = velocity, AngularVelocity = angularVelocity
            };
            revision++;
            return true;
        }

        public bool TrySample(float now, out VehicleMotionPose pose)
        {
            pose = new VehicleMotionPose();
            if (count == 0 || !V3.Finite(now)) return false;
            VehicleMotionPose last = At(count - 1);
            float elapsed = played ? Math.Max(0f, now - playedAt) : 0f;
            // Stop the playback clock with the bounded prediction. Otherwise a
            // resumed stream jumps straight to an already-advanced clock.
            float time = Math.Min(now - clockOffset - Delay, last.Time + MaxPrediction);
            // A new minimum transit time improves the clock estimate, but must
            // not advance the presentation by an entire jitter correction in a
            // single frame. Catch up at at most 110% of real time.
            if (played) time = Math.Max(playback, Math.Min(time, playback + Math.Max(0f, now - playedAt) * 1.1f));
            playback = time; playedAt = now; played = true;
            pose = Evaluate(time);
            float curveTime = pose.Time;
            if (hasOutput)
            {
                if (curveTime == outputCurveTime && revision == outputRevision)
                { pose.Position = output.Position; pose.Rotation = output.Rotation; }
                else
                {
                    // A new snapshot can refine an already-predicted segment.
                    // Carry its presentation error forward and decay it, so
                    // receipt cannot jump the car even when prediction overshot.
                    VehicleMotionPose previous = Evaluate(outputCurveTime);
                    V3 advance = Add(pose.Position, Scale(previous.Position, -1f));
                    float keep = (float)Math.Exp(-8f * elapsed);
                    pose.Position = Add(pose.Position, Scale(Add(output.Position, Scale(previous.Position, -1f)), keep));
                    // A correction must not pull the presentation against a
                    // still-advancing sampled path. A genuinely reversing path
                    // changes this direction and remains free to move back.
                    float distance2 = Dot(advance, advance);
                    float backward = Dot(Add(pose.Position, Scale(output.Position, -1f)), advance);
                    if (distance2 > .00000001f && backward < 0f)
                        pose.Position = Add(pose.Position, Scale(advance, -backward / distance2));
                    Q4 inverse = new Q4(-previous.Rotation.X, -previous.Rotation.Y, -previous.Rotation.Z, previous.Rotation.W);
                    Q4 error = Multiply(output.Rotation, inverse);
                    pose.Rotation = Multiply(Slerp(Q4.Identity, error, keep), pose.Rotation);
                }
            }
            output = pose; outputCurveTime = curveTime; outputRevision = revision; hasOutput = true;
            // Keep the older end of the segment. Consumed history cannot grow
            // with a long session, or be replayed when a packet arrives late.
            while (count > 2 && At(1).Time <= time) { start = (start + 1) % samples.Length; count--; }
            return true;
        }

        VehicleMotionPose Evaluate(float time)
        {
            VehicleMotionPose pose;
            VehicleMotionPose last = At(count - 1);
            VehicleMotionPose first = At(0);
            if (time <= first.Time) return first;
            if (time >= last.Time)
            {
                float dt = Math.Min(time - last.Time, MaxPrediction);
                pose = last; pose.Time = last.Time + dt;
                pose.Position = Add(last.Position, Scale(last.Velocity, dt));
                pose.Rotation = Advance(last.Rotation, last.AngularVelocity, dt);
                return pose;
            }
            for (int i = 1; i < count; i++)
            {
                VehicleMotionPose right = At(i);
                if (right.Time < time) continue;
                VehicleMotionPose left = At(i - 1);
                float t = (time - left.Time) / (right.Time - left.Time);
                pose = new VehicleMotionPose { Time = time, Position = Lerp(left.Position, right.Position, t),
                    Rotation = Slerp(left.Rotation, right.Rotation, t), Velocity = Lerp(left.Velocity, right.Velocity, t),
                    AngularVelocity = Lerp(left.AngularVelocity, right.AngularVelocity, t) };
                return pose;
            }
            return last;
        }

        static V3 Add(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        static V3 Scale(V3 a, float t) { return new V3(a.X * t, a.Y * t, a.Z * t); }
        static V3 Lerp(V3 a, V3 b, float t) { return Add(Scale(a, 1f - t), Scale(b, t)); }
        static float Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        static float Dot(Q4 a, Q4 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W; }
        static Q4 Normalize(Q4 q)
        {
            float k = 1f / (float)Math.Sqrt(Dot(q, q));
            return new Q4(q.X * k, q.Y * k, q.Z * k, q.W * k);
        }
        static Q4 Slerp(Q4 a, Q4 b, float t)
        {
            float dot = Dot(a, b);
            if (dot < 0) { b = new Q4(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
            double x = 1f - t, y = t;
            if (dot < .9995f)
            {
                double angle = Math.Acos(Math.Min(1f, dot)), sin = Math.Sin(angle);
                x = Math.Sin((1f - t) * angle) / sin; y = Math.Sin(t * angle) / sin;
            }
            return Normalize(new Q4((float)(a.X * x + b.X * y), (float)(a.Y * x + b.Y * y),
                (float)(a.Z * x + b.Z * y), (float)(a.W * x + b.W * y)));
        }
        static Q4 Advance(Q4 q, V3 angular, float dt)
        {
            double speed = Math.Sqrt(angular.X * angular.X + angular.Y * angular.Y + angular.Z * angular.Z);
            if (speed < .000001 || dt == 0) return q;
            double k = Math.Sin(speed * dt * .5) / speed;
            Q4 d = new Q4((float)(angular.X * k), (float)(angular.Y * k), (float)(angular.Z * k), (float)Math.Cos(speed * dt * .5));
            // Angular velocity is in world space, as in the existing receiver.
            return Multiply(d, q);
        }
        static Q4 Multiply(Q4 a, Q4 b)
        {
            return Normalize(new Q4(a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z));
        }
    }
}
