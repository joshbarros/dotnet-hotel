export interface RoomType {
  id: string;
  code: string;
  name: string;
  city: string;
  totalRooms: number;
  rateCents: number;
  firstNight: string;
  openNights: number;
}

export interface Night {
  night: string;
  totalRooms: number;
  roomsLeft: number;
}

export type BookingStatus = 'Requested' | 'Held' | 'Confirmed' | 'Cancelled' | 'Rejected';

export interface Booking {
  id: string;
  roomTypeCode: string;
  reference: string;
  checkIn: string;
  checkOut: string;
  nights: number;
  rooms: number;
  totalCents: number;
  status: BookingStatus;
  reason: string | null;
  voucherNumber: string | null;
  createdAt: string;
  updatedAt: string;
  roomsReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
