// Optional example. Copy into Assets only if you want this UdonSharp component.
// Attach to the pickup GameObject; enable traceEnabled while diagnosing.
// To log an actual resource grant, call LogResourceGrant at the point where inventory accepts it.
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

public class UdonTraceExample : UdonSharpBehaviour
{
    public bool traceEnabled;
    public int resourceId;
    public int resourceAmount;

    public override void OnPickup() { Record("OnPickup"); }
    public override void OnDrop() { Record("OnDrop"); }
    public override void OnPickupUseDown() { Record("OnPickupUseDown"); }
    public override void OnOwnershipTransferred(VRCPlayerApi player) { Record("OnOwnershipTransferred"); }

    public void LogResourceGrant()
    {
        Record("ResourceGranted item=" + resourceId + " amount=" + resourceAmount);
    }
    private void Record(string eventName)
    {
        if (!traceEnabled) return;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        int ownerId = Utilities.IsValid(owner) ? owner.playerId : -1;
        Debug.Log("[WDMCP_EVENT] object=" + gameObject.name + " event=" + eventName
            + " owner=" + ownerId + " frame=" + Time.frameCount);
    }
}
