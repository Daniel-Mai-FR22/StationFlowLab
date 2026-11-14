using System;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Bounded ownership from render reservation through ordered writer completion. No Unity API calls.</summary>
    internal sealed class StationDecisionCaptureRing<T> where T : class
    {
        internal readonly struct Ticket
        {
            internal readonly int index;
            internal readonly long generation;
            internal Ticket(int index, long generation) { this.index = index; this.generation = generation; }
            internal bool IsValid => generation > 0;
        }
        enum State { Free, Reserved, Pending, Ready, Taken }
        sealed class Slot { internal State state; internal long generation, sequence; internal T value; }
        readonly object gate = new object();
        readonly Slot[] slots;
        long submitted, nextOutput;
        bool closed;
        internal int Capacity => slots.Length;
        internal int OwnedCount { get { lock (gate) return Count(State.Free, true); } }
        internal int PendingCount { get { lock (gate) return Count(State.Pending, false); } }
        internal bool HasUnqueued { get { lock (gate) return Count(State.Pending, false) + Count(State.Ready, false) > 0; } }
        internal StationDecisionCaptureRing(int capacity)
        {
            if (capacity < 1 || capacity > 4) throw new ArgumentOutOfRangeException(nameof(capacity));
            slots = new Slot[capacity]; for (int i = 0; i < slots.Length; ++i) slots[i] = new Slot();
        }
        int Count(State state, bool invert) { int count = 0; foreach (var slot in slots) if ((slot.state == state) != invert) ++count; return count; }
        Slot Require(Ticket ticket)
        {
            if (!ticket.IsValid || ticket.index < 0 || ticket.index >= slots.Length || slots[ticket.index].generation != ticket.generation)
                throw new InvalidOperationException("Stale capture slot ownership.");
            return slots[ticket.index];
        }
        internal bool TryReserve(out Ticket ticket)
        {
            lock (gate)
            {
                if (!closed) for (int i = 0; i < slots.Length; ++i) if (slots[i].state == State.Free)
                { var slot = slots[i]; slot.state = State.Reserved; slot.value = null; ticket = new Ticket(i, ++slot.generation); return true; }
                ticket = default; return false;
            }
        }
        internal bool CancelReservation(Ticket ticket)
        {
            lock (gate)
            {
                if (!ticket.IsValid || ticket.index < 0 || ticket.index >= slots.Length) return false;
                var slot = slots[ticket.index];
                if (slot.generation != ticket.generation || slot.state != State.Reserved) return false;
                slot.state = State.Free; return true;
            }
        }
        internal void Submit(Ticket ticket)
        {
            lock (gate)
            {
                var slot = Require(ticket);
                if (closed || slot.state != State.Reserved) throw new InvalidOperationException("Capture slot was not reserved.");
                slot.sequence = submitted++; slot.state = State.Pending;
            }
        }
        internal void Complete(Ticket ticket, T value)
        {
            lock (gate)
            {
                var slot = Require(ticket);
                if (slot.state != State.Pending) throw new InvalidOperationException("Capture slot completed more than once.");
                slot.value = value; slot.state = State.Ready;
            }
        }
        /// <summary>Null completions are failed-frame tombstones and release their slot in original render order.</summary>
        internal bool TryTakeReady(out Ticket ticket, out T value)
        {
            lock (gate)
            {
                while (nextOutput < submitted)
                {
                    Slot next = null; int index = -1;
                    for (int i = 0; i < slots.Length; ++i)
                        if ((slots[i].state == State.Pending || slots[i].state == State.Ready) && slots[i].sequence == nextOutput)
                        { next = slots[i]; index = i; break; }
                    if (next == null) throw new InvalidOperationException("Capture completion order lost an owned slot.");
                    if (next.state == State.Pending) break;
                    ++nextOutput;
                    if (next.value == null) { next.state = State.Free; continue; }
                    ticket = new Ticket(index, next.generation); value = next.value; next.value = null; next.state = State.Taken; return true;
                }
                ticket = default; value = null; return false;
            }
        }
        internal void Release(Ticket ticket)
        {
            lock (gate)
            {
                var slot = Require(ticket);
                if (slot.state != State.Taken) throw new InvalidOperationException("Capture slot is not owned by the writer.");
                slot.value = null; slot.state = State.Free;
            }
        }
        internal void CloseAdmission()
        {
            lock (gate)
            {
                closed = true;
                foreach (var slot in slots) if (slot.state == State.Reserved) slot.state = State.Free;
            }
        }
    }
}
