namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHERE A SUBMIT RAISES ITS END-OF-REPLAY SIGNAL (decision C5, section 10.3), and the whole of what the
    /// submit path knows about fences. <see cref="D3D11FenceSubsystem"/> is the one implementation, and the
    /// device hands it to <see cref="D3D11CommandDrivers.Submit{TEmitter}"/> once it exists.
    /// <para>
    /// AN INTERFACE RATHER THAN THE SUBSYSTEM ITSELF, for the same two reasons
    /// <see cref="ID3D11DeviceLiveness"/> is one. It keeps the driver's submit path drivable with no timeline and
    /// no device behind it, so a device-free test can assert WHERE in a replay the signal lands and WHETHER the
    /// submit lock was held for it, which is the half of decision C5 no fence-lifecycle test can see. And it
    /// keeps the drivers from naming the fence subsystem at all, so the one place that knows both drivers exist
    /// stays a file about recording rather than about completion.
    /// </para>
    /// <para>
    /// THE SINK IS OPTIONAL ON THE SUBMIT PATH. A submit that names none replays and signals nothing, which is
    /// every device-free driver test and nothing else: a real device always passes its fence subsystem.
    /// A submit that carries a FENCE and no sink is refused instead of accepted quietly, because a fence nobody
    /// arms never reads signalled and a consumer polling it waits forever.
    /// </para>
    /// </summary>
    internal interface ID3D11SubmitSignal
    {
        /// <summary>
        /// Advance the timeline by one for the submission that has just finished emitting, arm
        /// <paramref name="fence"/> with that value if the submission carried one, and return the value.
        /// <para>
        /// CALL IT ONCE PER SUBMIT, AFTER the last command of that submission has been emitted and while the
        /// device's submit lock is held. A fenceless submit signals too.
        /// <see cref="D3D11FenceSubsystem.SignalEndOfReplay"/> carries what each of those three buys.
        /// </para>
        /// </summary>
        /// <param name="fence">The fence handed to <c>Submit(IGpuCommandList, IGpuFence)</c>, or null for the
        /// fenceless overload.</param>
        /// <returns>The timeline value this submission signalled.</returns>
        ulong SignalEndOfReplay(IGpuFence? fence);
    }
}
