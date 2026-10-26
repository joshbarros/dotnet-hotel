import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Booking, LogEntry, Night, RoomType } from './models';

export interface NewRoomType {
  code: string;
  name: string;
  city: string;
  totalRooms: number;
  rateCents: number;
  openNights: number;
}

export interface NewBooking {
  roomTypeCode: string;
  reference: string;
  checkIn: string;
  nights: number;
  rooms: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  roomTypes = () => firstValueFrom(this.http.get<RoomType[]>('/api/room-types'));
  addRoomType = (body: NewRoomType) =>
    firstValueFrom(this.http.post<RoomType>('/api/room-types', body));
  availability = (code: string, from: string, days: number) =>
    firstValueFrom(
      this.http.get<Night[]>(`/api/room-types/${code}/availability`, {
        params: { from, days },
      }),
    );

  bookings = (roomTypeCode: string) =>
    firstValueFrom(this.http.get<Booking[]>('/api/bookings', { params: { roomTypeCode } }));
  book = (body: NewBooking) => firstValueFrom(this.http.post<Booking>('/api/bookings', body));
  cancel = (id: string) =>
    firstValueFrom(this.http.post<Booking>(`/api/bookings/${id}/cancellation`, {}));
  events = (id: string) => firstValueFrom(this.http.get<LogEntry[]>(`/api/bookings/${id}/events`));
}
