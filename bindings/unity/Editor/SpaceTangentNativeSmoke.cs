// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using UnityEngine;

namespace SpaceTangent.Unity
{
    public static class SpaceTangentNativeSmoke
    {
        public static void Run()
        {
            if (SpaceTangentEphemeris.AbiVersion != 0x10000)
                throw new InvalidOperationException("Unexpected SpaceTangent native ABI version.");

            using var model = new SpaceTangentEphemeris(Environment.GetEnvironmentVariable("SPACETANGENT_DE440"));
            if (model.GetState(2451545, 0, SolarObject.Moon, SolarObject.Earth, out var state) != EphemerisStatus.Ok ||
                !Within(state.XKilometers, -291608.38463343546, 5e-6) ||
                !Within(state.YKilometers, -266716.83339423337, 5e-6) ||
                !Within(state.ZKilometers, -76102.48709990202, 5e-6) ||
                !Within(state.VxKilometersPerSecond, 0.64353138771903273, 5e-10) ||
                !Within(state.VyKilometersPerSecond, -0.66608768409163044, 5e-10) ||
                !Within(state.VzKilometersPerSecond, -0.30132570498227307, 5e-10))
                throw new InvalidOperationException("Native Moon state differs from the CSPICE reference.");

            Debug.Log("SpaceTangent Unity native ephemeris smoke passed");
        }

        private static bool Within(double actual, double expected, double tolerance) =>
            Math.Abs(actual - expected) <= tolerance;
    }
}
