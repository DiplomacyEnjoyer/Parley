// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Security.Cryptography;
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Stands in for one player's headset or browser in tests and simulations of sealed
/// orders: it keeps the order sheet and the salt to itself, publishes only the seal, and
/// opens the sheet when the flow asks. It does what a well-behaved device does; tests
/// that model cheating or a dropped connection call the flow directly instead.
/// </summary>
public sealed class SealedDevice
{
    private readonly GameFlow flow;
    private readonly OrderSeal seal;
    public readonly int Power;
    /// <summary>The private order sheet (GameCodec order text). Never sent before the reveal.</summary>
    public string Sheet = "";
    public string Salt = "";
    private int sealedSerial = -1;

    public SealedDevice(Game g, int power) { flow = g.Flow; seal = g.Seal; Power = power; }

    public static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Flips the submit toggle on: seals the sheet as it is now.</summary>
    public bool SubmitOn()
    {
        Salt = NewSalt();
        sealedSerial = flow.phaseSerial;
        return flow.SetSeal(Power, seal.Make(Salt, Power, flow.SealKey(), Sheet));
    }

    /// <summary>Flips the submit toggle off to keep editing.</summary>
    public bool SubmitOff() => flow.SetSubmitted(Power, false);

    /// <summary>
    /// What a device does every moment: when the clock has run out it seals whatever is
    /// on the sheet, and when seals are frozen it opens the sheet.
    /// </summary>
    public void React()
    {
        if (flow.stage == GameFlow.STAGE_CLOSING && flow.sealText[Power] == "" && flow.NeedsOrders(Power)) SubmitOn();
        if (flow.stage == GameFlow.STAGE_REVEAL && flow.sealText[Power] != "" && !flow.opened[Power] && sealedSerial == flow.phaseSerial)
            flow.Open(Power, Salt, Sheet);
    }
}
