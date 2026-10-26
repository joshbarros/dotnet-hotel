#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5216}"
RABBIT="${RABBIT:-http://127.0.0.1:15706}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }
day() { python3 -c "import datetime; print(datetime.date.today() + datetime.timedelta(days=$1))"; }

open_room_type() { # code totalRooms rateCents openNights -> prints nothing
  curl -sf -X POST "$API/api/room-types" -H "$JSON" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Room\",\"city\":\"Lisbon\",\"totalRooms\":$2,\"rateCents\":$3,\"openNights\":$4}" >/dev/null \
    || fail "could not open room type $1"
}
book() { # code reference checkIn nights rooms -> prints booking id
  curl -sf -X POST "$API/api/bookings" -H "$JSON" \
    -d "{\"roomTypeCode\":\"$1\",\"reference\":\"$2\",\"checkIn\":\"$3\",\"nights\":$4,\"rooms\":$5}" | jget "['id']"
}
booking_field() { curl -sf "$API/api/bookings/$1" | jget "['$2']"; }
nights_left() { # code from days -> "3 3 1 1" (rooms left per night)
  curl -sf "$API/api/room-types/$1/availability?from=$2&days=$3" | python3 -c "import sys,json; print(' '.join(str(n['roomsLeft']) for n in json.load(sys.stdin)))"
}

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(booking_field "$1" status)" = "$2" ]; }
left_is() { [ "$(nights_left "$1" "$2" "$3")" = "$4" ]; }
terminal() { case "$(booking_field "$1" status)" in Confirmed|Rejected|Cancelled) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/bookings/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/vouchers.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"
D1="$(day 1)"; D2="$(day 2)"; D3="$(day 3)"; D4="$(day 4)"; D5="$(day 5)"

echo "1. input is validated at the door"
open_room_type "V-$RUN" 5 12000 3
for body in \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"\",\"checkIn\":\"$D1\",\"nights\":1,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"nights\":1,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$(day -1)\",\"nights\":1,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$D1\",\"nights\":0,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$D1\",\"nights\":15,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$D1\",\"nights\":1,\"rooms\":4}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$D1\",\"nights\":4294967297,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$D1\",\"nights\":9223372036854775807,\"rooms\":1}" \
  "{\"roomTypeCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"checkIn\":\"$D2\",\"nights\":5,\"rooms\":1}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/bookings" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty reference, missing or past check-in, zero or overflowing nights, too many rooms and a stay past the last open night are all 400"

echo "2. happy path"
open_room_type "H-$RUN" 3 12000 30
ID="$(book "H-$RUN" "GUEST-$RUN-1" "$D2" 3 2)"
wait_for "booking to be Confirmed" status_is "$ID" Confirmed
[ "$(booking_field "$ID" totalCents)" = 72000 ] || fail "total should be 12000 x 3 nights x 2 rooms"
[ "$(curl -sf "$API/api/vouchers/$ID" | jget "['status']")" = Issued ] || fail "voucher not issued"
wait_for "three nights to drop to 1 room" left_is "H-$RUN" "$D2" 4 "1 1 1 3"
TYPES="$(event_types "$ID")"
for t in rooms.reserve rooms.held voucher.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "confirmed, total 72000, nights $D2..$D4 at 1 room left and the night after untouched, Kafka log has the three steps"

echo "3. last room: three identical stays, one commits; an overlapping stay is short and leaves every other night alone"
open_room_type "L-$RUN" 1 12000 30
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "GUEST-$RUN-$n" "$D1" 3 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "booking $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "booking $i to settle" terminal "$i"; done
CONFIRMED=0; SHORT=0
for i in $IDS; do
  s="$(booking_field "$i" status)"
  if [ "$s" = Confirmed ]; then CONFIRMED=$((CONFIRMED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(booking_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *rooms.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$CONFIRMED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 confirmed and 2 short, got $CONFIRMED confirmed and $SHORT short"
wait_for "stay nights to be sold out" left_is "L-$RUN" "$D1" 5 "0 0 0 1 1"
IDO="$(book "L-$RUN" "GUEST-$RUN-O" "$D3" 3 1)"
wait_for "overlapping booking to settle" terminal "$IDO"
status_is "$IDO" Rejected || fail "the overlapping stay should be short"
[ "$(nights_left "L-$RUN" "$D1" 5)" = "0 0 0 1 1" ] || fail "the short stay changed other nights: $(nights_left "L-$RUN" "$D1" 5)"
say "1 confirmed, 2 short; the stay overlapping the sold-out night $D3 is short and nights $D4 and $D5 stay at 1 room"

echo "4. poison: committed as rejected, dead-lettered, every night comes back"
open_room_type "P-$RUN" 2 12000 30
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "GUEST-POISON-$RUN" "$D1" 2 2)"
wait_for "poison booking to be Rejected" status_is "$IDP" Rejected
wait_for "nights to be restored to 2 rooms" left_is "P-$RUN" "$D1" 3 "2 2 2"
wait_for "vouchers.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/vouchers/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the voucher row"
[ "$(booking_field "$IDP" roomsReleasedAt)" != None ] || fail "booking should record the release"
say "rejected, dead-lettered (+1 in vouchers.work.dlq), both nights back at 2 rooms"

echo "5. cancellation races the final mark: the cancellation wins, the issued voucher stays"
open_room_type "R-$RUN" 4 12000 30
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "GUEST-$RUN-R$attempt" "$D1" 3 1)"
  # The voucher exists before the booking is marked Confirmed (the final mark is delayed in compose).
  wait_for "voucher to exist" curl -sf "$API/api/vouchers/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (booking already Confirmed); the cancellation still has to hold; retrying"
  curl -sf -X POST "$API/api/bookings/$IDR/cancellation" >/dev/null || true
  wait_for "booking to be Cancelled" status_is "$IDR" Cancelled
done
[ "$WON" = 1 ] || fail "never caught the booking between voucher issued and final mark"
curl -sf -X POST "$API/api/bookings/$IDR/cancellation" >/dev/null || fail "cancellation was refused"
wait_for "booking to be Cancelled" status_is "$IDR" Cancelled
sleep 3 # longer than the final-mark delay: the late mark must not undo the cancellation
status_is "$IDR" Cancelled || fail "the final mark overwrote the cancellation"
[ "$(curl -sf "$API/api/vouchers/$IDR" | jget "['status']")" = Issued ] || fail "the issued voucher was rolled back"
[ "$(booking_field "$IDR" voucherNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "every night to come back" left_is "R-$RUN" "$D1" 4 "4 4 4 4"
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/bookings/$IDR/cancellation")"
[ "$code" = 409 ] || fail "second cancellation should be 409, got $code"
say "booking Cancelled, voucher still Issued, all nights back at 4 rooms, second cancellation 409"

echo "hotel smoke passed"
