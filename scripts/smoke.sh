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

ADMIN_EMAIL="${ADMIN_EMAIL:-admin@hotel.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-HotelAdmin2026}"
CUSTOMER_PASSWORD="Smoke-Passw0rd1"
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
token_of() { python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])"; }

# Section 0 only needs "a small roomType" and "one booking"; a system with another shape adapts these two lines.
open_small() { open_room_type "$1" 5 12000 30; }
book_one() { book "$1" "$2" "$(day 2)" 1 1; }

open_room_type() { # code totalRooms rateCents openNights -> prints nothing
  curl -sf -X POST "$API/api/room-types" -H "$JSON" -H "$ADM" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Room\",\"city\":\"Lisbon\",\"totalRooms\":$2,\"rateCents\":$3,\"openNights\":$4}" >/dev/null \
    || fail "could not open room type $1"
}
book() { # code reference checkIn nights rooms -> prints booking id
  curl -sf -X POST "$API/api/bookings" -H "$JSON" -H "$CUS" \
    -d "{\"roomTypeCode\":\"$1\",\"reference\":\"$2\",\"checkIn\":\"$3\",\"nights\":$4,\"rooms\":$5}" | jget "['id']"
}
booking_field() { curl -sf -H "$ADM" "$API/api/bookings/$1" | jget "['$2']"; }
nights_left() { # code from days -> "3 3 1 1" (rooms left per night)
  curl -sf -H "$ADM" "$API/api/room-types/$1/availability?from=$2&days=$3" | python3 -c "import sys,json; print(' '.join(str(n['roomsLeft']) for n in json.load(sys.stdin)))"
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
event_types() { curl -sf -H "$ADM" "$API/api/bookings/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/vouchers.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "0. authentication and ownership"
[ "$(code_of "$API/api/room-types")" = 401 ] || fail "an anonymous request should be 401"
[ "$(code_of -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"wrong-Passw0rd\"}")" = 401 ] || fail "a wrong password should be 401"
ADM="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" | token_of)" || fail "the administrator could not sign in"
CUS="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"smoke-$RUN@example.com\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)" || fail "could not register a customer"
OTHER="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"other-$RUN@example.com\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)"
[ "$(code_of -X POST "$API/api/room-types" -H "$JSON" -H "$CUS" -d '{"code":"X"}')" = 403 ] || fail "a customer must not open roomTypes"
open_small "A-$RUN"
IDA="$(book_one "A-$RUN" "GUEST-$RUN-OWN")"
[ "$(code_of -H "$OTHER" "$API/api/bookings/$IDA")" = 404 ] || fail "another customer must get 404 for someone else's booking"
[ "$(code_of -H "$CUS" "$API/api/bookings/$IDA/events")" = 403 ] || fail "the event log is staff only"
[ "$(code_of -X POST -H "$OTHER" "$API/api/bookings/$IDA/cancellation")" = 404 ] || fail "another customer must not cancel it"
say "401 anonymous, 401 bad password, 403 customer opening a roomType, 404 for another customer, 403 on the event log"
D1="$(day 1)"; D2="$(day 2)"; D3="$(day 3)"; D4="$(day 4)"; D5="$(day 5)"

echo "0b. password reset ends the old sessions (needs the dev mailbox the demo stack turns on)"
RESET_EMAIL="reset-$RUN@example.com"
OLD="$(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"$RESET_EMAIL\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)"
[ "$(code_of -H "Authorization: Bearer $OLD" "$API/api/auth/me")" = 200 ] || fail "the new session should work"
[ "$(code_of -X POST "$API/api/auth/forgot-password" -H "$JSON" -d "{\"email\":\"nobody-$RUN@example.com\"}")" = 202 ] || fail "an unknown address must look the same"
curl -sf -X POST "$API/api/auth/forgot-password" -H "$JSON" -d "{\"email\":\"$RESET_EMAIL\"}" >/dev/null || fail "forgot-password failed"
LINK="$(curl -sf "$API/api/dev/mailbox?to=$RESET_EMAIL" | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['link'])")"
link_param() { python3 -c "import sys,urllib.parse as u; print(u.parse_qs(u.urlparse(sys.argv[1]).query)['$1'][0])" "$LINK"; }
[ "$(code_of -X POST "$API/api/auth/reset-password" -H "$JSON" -d "{\"userId\":\"$(link_param reset)\",\"token\":\"$(link_param token)\",\"newPassword\":\"Another-Passw0rd9\"}")" = 204 ] || fail "reset failed"
sleep 3 # the session version is cached for a moment
[ "$(code_of -H "Authorization: Bearer $OLD" "$API/api/auth/me")" = 401 ] || fail "the old access token should be revoked"
[ "$(code_of -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$RESET_EMAIL\",\"password\":\"Another-Passw0rd9\"}")" = 200 ] || fail "the new password should work"
say "reset link works once, the old access token is revoked, the new password signs in"

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
  code="$(code_of -X POST "$API/api/bookings" -H "$JSON" -H "$CUS" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty reference, missing or past check-in, zero or overflowing nights, too many rooms and a stay past the last open night are all 400"

echo "2. happy path"
open_room_type "H-$RUN" 3 12000 30
ID="$(book "H-$RUN" "GUEST-$RUN-1" "$D2" 3 2)"
wait_for "booking to be Confirmed" status_is "$ID" Confirmed
[ "$(booking_field "$ID" totalCents)" = 72000 ] || fail "total should be 12000 x 3 nights x 2 rooms"
[ "$(curl -sf -H "$ADM" "$API/api/vouchers/$ID" | jget "['status']")" = Issued ] || fail "voucher not issued"
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
[ "$(curl -sf -H "$ADM" "$API/api/vouchers/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the voucher row"
[ "$(booking_field "$IDP" roomsReleasedAt)" != None ] || fail "booking should record the release"
say "rejected, dead-lettered (+1 in vouchers.work.dlq), both nights back at 2 rooms"

echo "4b. dead letters can be listed by staff only, and replayed"
[ "$(code_of -H "$CUS" "$API/api/admin/dead-letters")" = 403 ] || fail "dead letters are staff only"
curl -sf -H "$ADM" "$API/api/admin/dead-letters" | python3 -c "import sys,json; q={x['queue']:x for x in json.load(sys.stdin)}; assert q['vouchers.work']['count']>=1, q" || fail "the dead-letter queue should list the poison message"
BEFORE_REPLAY="$(dlq_count)"
REPLAYED="$(curl -sf -X POST -H "$ADM" "$API/api/admin/dead-letters/vouchers.work/replay?max=100" | jget "['replayed']")" || fail "replay failed"
[ "$REPLAYED" -ge 1 ] || fail "replay should have taken at least one message"
# The poison reference still fails for the same reason, so it is retried and dead-lettered again: replay is for fixed causes.
wait_for "the replayed poison message to be dead-lettered again" dlq_is "$BEFORE_REPLAY"
say "listed, replayed $REPLAYED, dead-lettered again because the cause is still there"

echo "5. cancellation races the final mark: the cancellation wins, the issued voucher stays"
open_room_type "R-$RUN" 4 12000 30
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "GUEST-$RUN-R$attempt" "$D1" 3 1)"
  # The voucher exists before the booking is marked Confirmed (the final mark is delayed in compose).
  wait_for "voucher to exist" curl -sf -H "$ADM" "$API/api/vouchers/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (booking already Confirmed); the cancellation still has to hold; retrying"
  curl -sf -X POST -H "$CUS" "$API/api/bookings/$IDR/cancellation" >/dev/null || true
  wait_for "booking to be Cancelled" status_is "$IDR" Cancelled
done
[ "$WON" = 1 ] || fail "never caught the booking between voucher issued and final mark"
curl -sf -X POST -H "$CUS" "$API/api/bookings/$IDR/cancellation" >/dev/null || fail "cancellation was refused"
wait_for "booking to be Cancelled" status_is "$IDR" Cancelled
sleep 3 # longer than the final-mark delay: the late mark must not undo the cancellation
status_is "$IDR" Cancelled || fail "the final mark overwrote the cancellation"
[ "$(curl -sf -H "$ADM" "$API/api/vouchers/$IDR" | jget "['status']")" = Issued ] || fail "the issued voucher was rolled back"
[ "$(booking_field "$IDR" voucherNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "every night to come back" left_is "R-$RUN" "$D1" 4 "4 4 4 4"
code="$(code_of -X POST -H "$CUS" "$API/api/bookings/$IDR/cancellation")"
[ "$code" = 409 ] || fail "second cancellation should be 409, got $code"
say "booking Cancelled, voucher still Issued, all nights back at 4 rooms, second cancellation 409"

echo "hotel smoke passed"
