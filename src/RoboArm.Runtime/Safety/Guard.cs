using RoboArm.Machine;
using RoboArm.Motion;
using RoboArm.Safety;

namespace RoboArm.Runtime.Safety;

/// <summary>
/// The Guard (docs/03 L3): every frame passes through here immediately before dispatch.
/// Each interlock trips independently; any violation blocks the frame and returns the reason.
/// </summary>
public static class Guard
{
    public static bool CheckFrame(
        RuntimeState state,
        MachineConfig config,
        MotionFrame frame,
        double speedOverride,
        out GuardViolation violation)
    {
        // Interlock 1: motion frames only while Executing.
        if (state != RuntimeState.Executing)
        {
            violation = GuardViolation.WrongState;
            return false;
        }

        foreach (var setpoint in frame.Setpoints)
        {
            var axis = config.Axis(setpoint.AxisId);
            if (axis is null)
            {
                violation = GuardViolation.UnknownAxis;
                return false;
            }

            // Interlock 2: setpoints within soft limits.
            if (setpoint.PositionDeg < axis.SoftLimitMinDeg - 0.001
                || setpoint.PositionDeg > axis.SoftLimitMaxDeg + 0.001)
            {
                violation = GuardViolation.SoftLimit;
                return false;
            }

            // Interlock 3: velocity within axis capability × override.
            var cap = axis.MaxVelocityDegS * Math.Clamp(speedOverride, 0, 1) + 0.001;
            if (Math.Abs(setpoint.VelocityDegS) > cap)
            {
                violation = GuardViolation.SpeedCap;
                return false;
            }

            // Interlock 4: decel margin — stopping distance must fit inside the soft limit.
            var stoppingDist = setpoint.VelocityDegS * setpoint.VelocityDegS / (2 * axis.MaxAccelDegS2);
            var roomAhead = axis.SoftLimitMaxDeg - setpoint.PositionDeg;
            var roomBehind = setpoint.PositionDeg - axis.SoftLimitMinDeg;
            if (setpoint.VelocityDegS > 0 && roomAhead < stoppingDist - 0.01
                || setpoint.VelocityDegS < 0 && roomBehind < stoppingDist - 0.01)
            {
                violation = GuardViolation.DecelMargin;
                return false;
            }
        }

        violation = GuardViolation.None;
        return true;
    }
}

public enum GuardViolation
{
    None,
    WrongState,
    UnknownAxis,
    SoftLimit,
    SpeedCap,
    DecelMargin,
}
