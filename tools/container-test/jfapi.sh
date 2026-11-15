#!/bin/bash
# End-to-end test helper for the 9Now plugin inside a Jellyfin container.
# Usage: ./jfapi.sh login | ./jfapi.sh <curl-args...>
DIR="$(cd "$(dirname "$0")" && pwd)"
TOKEN_FILE="$DIR/.token"
CLIENT_HEADERS=(-H 'X-MediaBrowser-Client: curltest' -H 'X-MediaBrowser-ClientVersion: 1' -H 'X-MediaBrowser-Device: testdev' -H 'X-MediaBrowser-DeviceId: testdev1')
AUTH_HEADER='Authorization: MediaBrowser Client="curltest", Device="testdev", DeviceId="testdev1", Version="1"'
login() {
  TOKEN=$(curl -s -X POST http://127.0.0.1:8096/Users/AuthenticateByName \
    -H 'Content-Type: application/json' -H "$AUTH_HEADER" \
    -d '{"Username":"admin","Pw":"NineNowTest1!"}' \
    | python3 -c 'import sys,json; print(json.load(sys.stdin)["AccessToken"])')
  echo "$TOKEN" > "$TOKEN_FILE"
  echo "token: ${TOKEN:0:14}..."
}
if [ "${1:-}" = "login" ]; then login; exit 0; fi
TOKEN=$(cat "$TOKEN_FILE" 2>/dev/null)
curl -s -H "Authorization: MediaBrowser Client=\"curltest\", Device=\"testdev\", DeviceId=\"testdev1\", Version=\"1\", Token=\"$TOKEN\"" "$@"
