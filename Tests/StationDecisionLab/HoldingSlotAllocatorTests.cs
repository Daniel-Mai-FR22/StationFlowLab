using System;
using System.Linq;
using StationFlow.TrainStation.DecisionLab;

static class HoldingSlotAllocatorTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("128 actor owners cannot alias one of 64 leased holding markers", DoubledPool);
        run("a holding lease stays stable while its journey approaches", StableLease);
        run("reusing an actor releases only its previous journey's holding marker", RecycledOwner);
        run("released holding markers wait for physical departure clearance", DepartureClearance);
        run("inactive and finished journeys release their own leases without moving others", StaleCleanup);
        run("holding previews and exhausted or invalid requests cannot invent reservations", BoundedPreview);
    }
    static void Check(bool yes, string reason) { if (!yes) throw new InvalidOperationException(reason); }
    static void DoubledPool()
    {
        var allocator = new DecisionHoldingSlotAllocator(64);
        for (int i = 0; i < 128; ++i)
        {
            bool acquired = allocator.TryAcquire("J" + i, i + 1, i % 64, _ => true, out int slot);
            Check(acquired == (i < 64) && (acquired || slot == -1), "Exhausted capacity reused a leased holding goal.");
        }
        Check(allocator.Count == 64 && Enumerable.Range(0, 64).Select(i => allocator.SlotFor("J" + i)).Distinct().Count() == 64,
            "Doubled pool owners alias physical markers.");
        allocator.Release("J10");
        Check(allocator.TryAcquire("J74", 75, 10, _ => true, out int released) && released == 10 && allocator.SlotFor("J10") == -1,
            "A new owner could not use a safely released marker.");
        var pair = new DecisionHoldingSlotAllocator(64);
        Check(pair.TryAcquire("slot010", 11, 10, _ => true, out int first) &&
            pair.TryAcquire("slot074", 75, 10, _ => true, out int second) && first != second,
            "The recorded 010/074 modulo collision persists.");
    }
    static void StableLease()
    {
        var allocator = new DecisionHoldingSlotAllocator(3);
        Check(allocator.TryAcquire("A", 1, 1, _ => true, out int first), "Initial holding lease rejected.");
        Check(allocator.TryAcquire("A", 1, 0, _ => false, out int repeat) && repeat == first && allocator.Count == 1,
            "Changing preference or a passing body moved an approaching journey's lease.");
        Check(!allocator.TryAcquire("A", 2, 0, _ => true, out _), "Another actor stole the same journey's marker.");
    }
    static void RecycledOwner()
    {
        var allocator = new DecisionHoldingSlotAllocator(2);
        allocator.TryAcquire("old", 1, 0, _ => true, out _); allocator.TryAcquire("neighbor", 2, 1, _ => true, out int neighbor);
        Check(allocator.TryAcquire("new", 1, 0, _ => true, out int revised) && revised == 0 && allocator.SlotFor("old") == -1 &&
            allocator.SlotFor("neighbor") == neighbor && allocator.Count == 2, "Actor reuse retained a ghost lease or displaced its neighbor.");
        Check(!allocator.Release("old") && allocator.SlotFor("new") == revised, "Old identity cleanup removed the new journey's marker.");
    }
    static void DepartureClearance()
    {
        var allocator = new DecisionHoldingSlotAllocator(2);
        allocator.TryAcquire("joiningFIFO", 1, 0, _ => true, out _); allocator.Release("joiningFIFO");
        Check(allocator.TryAcquire("B", 2, 0, i => i != 0, out int next) && next == 1,
            "A released marker was assigned while its old physical body still occupied it.");
        Check(allocator.TryAcquire("C", 3, 0, _ => true, out int cleared) && cleared == 0,
            "A physically cleared released marker remained unavailable.");
    }
    static void StaleCleanup()
    {
        var allocator = new DecisionHoldingSlotAllocator(3);
        allocator.TryAcquire("finished", 1, 0, _ => true, out _); allocator.TryAcquire("inactive", 2, 1, _ => true, out _);
        allocator.TryAcquire("active", 3, 2, _ => true, out int active);
        var stale = allocator.Prune((id, owner) => id == "active" && owner == 3);
        Check(stale.Length == 2 && allocator.Count == 1 && allocator.SlotFor("active") == active,
            "Inactive/finished cleanup removed or retargeted a live neighbor.");
        allocator.Clear(); Check(allocator.Count == 0 && allocator.SlotFor("active") == -1, "Disabling the holding resource retained claims.");
    }
    static void BoundedPreview()
    {
        var allocator = new DecisionHoldingSlotAllocator(1);
        Check(allocator.TryPeek(int.MinValue, _ => true, out int preview) && preview == 0 && allocator.Count == 0,
            "Planning-only preview consumed a holding lease.");
        Check(!allocator.TryAcquire("", 1, 0, _ => true, out _) && !allocator.TryAcquire("A", 0, 0, _ => true, out _) &&
            !allocator.TryAcquire("A", 1, 0, _ => false, out _), "Malformed or physically blocked requests acquired a goal.");
        allocator.TryAcquire("A", 1, 0, _ => true, out _);
        Check(!allocator.TryPeek(0, _ => true, out _) && !allocator.TryAcquire("B", 2, 0, _ => true, out _) && allocator.Count == 1,
            "A full holding resource exposed somebody else's reserved goal.");
    }
}
