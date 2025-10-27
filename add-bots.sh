#!/bin/bash

# Smart bot addition script - fills server to 20 bots
# Usage: ./add-bots.sh [target_count]

API_URL="http://localhost:5100"
TARGET=${1:-20}  # Default to 20 bots (server maximum) if not specified

echo "Checking current player count..."

# Get current player list
RESPONSE=$(curl -s "$API_URL/api/server/players")

if [ $? -ne 0 ]; then
    echo "Error: Could not connect to API. Is the server running?"
    exit 1
fi

# Extract player count from JSON
CURRENT=$(echo "$RESPONSE" | grep -o '"totalPlayers":[0-9]*' | grep -o '[0-9]*')

if [ -z "$CURRENT" ]; then
    echo "Error: Could not parse player count from API response"
    exit 1
fi

echo "Current players: $CURRENT"
echo "Target players: $TARGET"

BOTS_NEEDED=$((TARGET - CURRENT))

if [ $BOTS_NEEDED -le 0 ]; then
    echo "Server already has $CURRENT players (target: $TARGET). No bots needed!"
    exit 0
fi

echo "Adding $BOTS_NEEDED bots..."

for ((i=1; i<=BOTS_NEEDED; i++)); do
    echo -n "Adding bot $i/$BOTS_NEEDED... "

    RESULT=$(curl -s -X POST "$API_URL/api/server/command" \
        -H "Content-Type: application/json" \
        -d '{"command": "/bot"}')

    if echo "$RESULT" | grep -q "success"; then
        echo "✓"
    else
        echo "✗ Failed"
    fi

    sleep 0.5  # Wait 500ms between bots
done

echo ""
echo "Done! Checking final count..."
sleep 1

# Verify final count
FINAL_RESPONSE=$(curl -s "$API_URL/api/server/players")
FINAL_COUNT=$(echo "$FINAL_RESPONSE" | grep -o '"totalPlayers":[0-9]*' | grep -o '[0-9]*')

echo "Final player count: $FINAL_COUNT"
