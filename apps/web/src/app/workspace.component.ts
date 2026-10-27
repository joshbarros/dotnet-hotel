import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { ApiService } from './core/api.service';
import { AuthService } from './core/auth.service';
import { Booking, BookingStatus, LogEntry, Night, RoomType } from './core/models';

const BADGES: Record<BookingStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Confirmed: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Cancelled: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

const isoDay = (offset: number): string =>
  new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

@Component({
  selector: 'app-workspace',
  templateUrl: './workspace.component.html',
})
export class WorkspaceComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  readonly roomTypes = signal<RoomType[]>([]);
  readonly selectedCode = signal('');
  readonly bookings = signal<Booking[]>([]);
  readonly nightsList = signal<Night[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly name = signal('Harbor Deluxe King');
  readonly city = signal('Lisbon');
  readonly totalRooms = signal('5');
  readonly rateCents = signal('12000');
  readonly openNights = signal('30');

  readonly reference = signal('');
  readonly checkIn = signal(isoDay(1));
  readonly nights = signal('2');
  readonly rooms = signal('1');

  readonly selected = computed(() => this.roomTypes().find((r) => r.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: BookingStatus): string {
    return BADGES[status];
  }

  cell(left: number): string {
    return left === 0
      ? 'bg-rose-50 text-rose-800 ring-rose-300'
      : 'bg-white text-slate-900 ring-slate-200';
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  shortDay(iso: string): string {
    return new Date(iso + 'T00:00:00Z').toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      timeZone: 'UTC',
    });
  }

  async refresh(): Promise<void> {
    try {
      const roomTypes = await this.api.roomTypes();
      this.roomTypes.set(roomTypes);
      if (!this.selectedCode() && roomTypes.length > 0) this.selectedCode.set(roomTypes[0].code);
      if (this.selectedCode()) {
        this.bookings.set(await this.api.bookings(this.selectedCode()));
        this.nightsList.set(await this.api.availability(this.selectedCode(), isoDay(0), 14));
      }
      const id = this.logFor();
      if (id && this.auth.isStaff()) this.log.set(await this.api.events(id));
    } catch (e: unknown) {
      // A 401 means the session is gone; the interceptor has already signed the user out.
      if ((e as { status?: number }).status !== 401) this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async addRoomType(): Promise<void> {
    await this.run(async () => {
      const code = this.code();
      const roomType = await this.api.addRoomType({
        code,
        name: this.name(),
        city: this.city(),
        totalRooms: Number(this.totalRooms()),
        rateCents: Number(this.rateCents()),
        openNights: Number(this.openNights()),
      });
      if (this.code() === code) this.code.set('');
      this.selectedCode.set(roomType.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      const reference = this.reference();
      await this.api.book({
        roomTypeCode: this.selectedCode(),
        reference,
        checkIn: this.checkIn(),
        nights: Number(this.nights()),
        rooms: Number(this.rooms()),
      });
      if (this.reference() === reference) this.reference.set('');
    });
  }

  async cancel(booking: Booking): Promise<void> {
    await this.run(() => this.api.cancel(booking.id));
  }

  async showLog(booking: Booking): Promise<void> {
    this.logFor.set(booking.id);
    this.log.set([]);
    await this.refresh();
  }

  canCancel(booking: Booking): boolean {
    return booking.status === 'Held' || booking.status === 'Confirmed';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
