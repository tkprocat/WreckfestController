# Events System Implementation TODO - C# WreckfestController

Implementation checklist for the Events system on the C# WreckfestController side.

**Status:** Not Started
**Last Updated:** January 2025

---

## Overview

The Events system allows Laravel to schedule server configurations (name, welcome message, track rotation) that the C# controller autonomously activates at the scheduled time with smart restart logic.

**Architecture:**
- Laravel pushes complete event schedule to C# via API
- C# stores schedule and runs internal timer
- C# activates events automatically at scheduled time
- C# uses "smart restart" to minimize player disruption
- C# webhooks Laravel when events activate

---

## Phase 1: Event Models & Data Structures

### ☐ Create Event Model Class
**File:** `Models/Event.cs`
```csharp
public class Event
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public DateTime StartTime { get; set; }
    public bool IsActive { get; set; }
    public ServerConfig ServerConfig { get; set; }
    public List<Track> Tracks { get; set; }
    public string CollectionName { get; set; }
    public RecurringPattern RecurringPattern { get; set; }
}
```

**Tasks:**
- [ ] Create Event class with all properties
- [ ] Add JSON serialization attributes
- [ ] Create ServerConfig nested class (name, welcome message, etc.)
- [ ] Create Track nested class for rotation
- [ ] Create RecurringPattern nested class

### ☐ Create EventSchedule Class
**File:** `Models/EventSchedule.cs`
```csharp
public class EventSchedule
{
    public List<Event> Events { get; set; }
    public DateTime LastUpdated { get; set; }
}
```

**Tasks:**
- [ ] Create EventSchedule class
- [ ] Add methods: LoadFromFile(), SaveToFile()
- [ ] Add method: GetUpcomingEvents()
- [ ] Add method: GetActiveEvent()
- [ ] Add method: GetNextEvent()

### ☐ File Persistence
**File:** `Services/EventStorageService.cs`

**Tasks:**
- [ ] Create EventStorageService class
- [ ] Implement JSON serialization/deserialization
- [ ] Save schedule to `Data/event-schedule.json`
- [ ] Load schedule on application startup
- [ ] Handle file not found gracefully
- [ ] Add logging for save/load operations

---

## Phase 2: API Endpoints

### ☐ POST /api/Events/schedule
**File:** `Controllers/EventsController.cs`

**Purpose:** Receive complete event schedule from Laravel

**Tasks:**
- [ ] Create EventsController
- [ ] Add POST schedule endpoint
- [ ] Validate incoming event data
- [ ] Replace existing schedule with new schedule
- [ ] Save schedule to file
- [ ] Return success/failure response
- [ ] Log schedule updates

**Request Body:**
```json
{
  "events": [
    {
      "id": 1,
      "name": "Weekend Racing",
      "description": "Special weekend event",
      "startTime": "2025-01-10T20:00:00Z",
      "isActive": false,
      "serverConfig": {
        "serverName": "Weekend Special",
        "welcomeMessage": "Welcome to weekend racing!"
      },
      "tracks": [...],
      "collectionName": "Weekend Rotation",
      "recurringPattern": null
    }
  ]
}
```

### ☐ GET /api/Events/current
**Purpose:** Get currently active event

**Tasks:**
- [ ] Add GET current endpoint
- [ ] Query active event from schedule
- [ ] Return event details or null
- [ ] Handle no active event case

### ☐ GET /api/Events/upcoming
**Purpose:** Get list of upcoming events

**Tasks:**
- [ ] Add GET upcoming endpoint
- [ ] Filter events: start_time >= now, not active
- [ ] Order by start time ascending
- [ ] Return list of events

### ☐ POST /api/Events/{id}/activate
**Purpose:** Manually activate an event (triggered from Laravel admin)

**Tasks:**
- [ ] Add POST activate endpoint
- [ ] Find event by ID
- [ ] Trigger smart restart service
- [ ] Apply event configuration after restart
- [ ] Webhook Laravel on completion
- [ ] Return success/failure

---

## Phase 3: Smart Restart System

### ☐ Create SmartRestartService
**File:** `Services/SmartRestartService.cs`

**Purpose:** Handle graceful server restarts with player warnings

**State Machine:**
```
Idle → Warning (T-5min) → Pending (T-0) → Restarting → Completed
```

**Tasks:**
- [ ] Create SmartRestartService class
- [ ] Add state enum (Idle, Warning, Pending, Restarting, Completed)
- [ ] Implement InitiateRestart(Event event) method
- [ ] Start 5-minute countdown timer
- [ ] Track current state

### ☐ Server Message System
**Purpose:** Send in-game messages to players

**Tasks:**
- [ ] Add SendServerMessage(string message) method
- [ ] Determine correct command format (check if `/message` or `message`)
- [ ] Write to server process stdin
- [ ] Add error handling
- [ ] Log all messages sent

### ☐ Countdown Timer Implementation
**Tasks:**
- [ ] Create Timer with 1-minute intervals
- [ ] Send warnings at T-5, T-4, T-3, T-2, T-1 minutes
- [ ] Use generic messages: "Server restarting in X minutes."
- [ ] At T-0: Change state to Pending
- [ ] Display message: "Server will restart at next lobby."

### ☐ Lobby Detection Logic
**Tasks:**
- [ ] Monitor player count (from existing player tracking)
- [ ] Listen for track-changed events (webhook from server)
- [ ] If no players online: restart immediately
- [ ] If players online: wait for track-changed event
- [ ] Set 10-minute maximum wait timeout
- [ ] Force restart after timeout

### ☐ Restart Execution
**Tasks:**
- [ ] Call existing server restart logic
- [ ] Send final message: "Server restarting now."
- [ ] Wait for server to stop
- [ ] Wait for server to start
- [ ] Verify server is running
- [ ] Call event activation callback

---

## Phase 4: Event Scheduler Service

### ☐ Create EventSchedulerService
**File:** `Services/EventSchedulerService.cs`

**Purpose:** Background service that checks for events to activate

**Tasks:**
- [ ] Create EventSchedulerService as hosted service
- [ ] Implement IHostedService interface
- [ ] Create Timer (check every 30 seconds)
- [ ] Load event schedule on startup
- [ ] Check for events where start_time <= now
- [ ] Trigger smart restart for due events
- [ ] Handle multiple events at same time (priority?)
- [ ] Add comprehensive logging

### ☐ Event Activation Flow
**Tasks:**
- [ ] Find next event to activate
- [ ] Log: "Event X scheduled to start"
- [ ] Call SmartRestartService.InitiateRestart(event)
- [ ] Wait for restart to complete
- [ ] Apply event configuration (next section)
- [ ] Mark event as active in memory
- [ ] Webhook Laravel with activation
- [ ] Handle recurring events (calculate next instance)

### ☐ Apply Event Configuration
**Tasks:**
- [ ] Update server name (call existing config API)
- [ ] Update welcome message
- [ ] Update any other server settings from event.ServerConfig
- [ ] Deploy track rotation
- [ ] Use existing config deployment methods
- [ ] Log all configuration changes

---

## Phase 5: Laravel Integration

### ☐ Webhook to Laravel
**Purpose:** Notify Laravel when event activates

**Tasks:**
- [ ] Create LaravelWebhookService class
- [ ] Add SendEventActivated(int eventId, string eventName) method
- [ ] HTTP POST to Laravel: `/api/webhooks/event-activated`
- [ ] Include: eventId, eventName in payload
- [ ] Add retry logic (3 attempts with exponential backoff)
- [ ] Log webhook attempts and results
- [ ] Handle network failures gracefully

**Webhook Payload:**
```json
{
  "eventId": 1,
  "eventName": "Weekend Racing"
}
```

### ☐ Track Player Count Updates
**Purpose:** Needed for smart restart logic

**Tasks:**
- [ ] Verify existing player tracking works
- [ ] Ensure player count is accessible to SmartRestartService
- [ ] Track count updates in real-time
- [ ] Log player count changes during restart

### ☐ Track Change Detection
**Purpose:** Detect when players return to lobby

**Tasks:**
- [ ] Verify track-changed webhook is working
- [ ] Make track change event accessible to SmartRestartService
- [ ] Subscribe to track change notifications
- [ ] Trigger restart when track changes during Pending state

---

## Phase 6: Recurring Events

### ☐ Recurring Pattern Model
**Tasks:**
- [ ] Add RecurringPattern class
- [ ] Properties: Type (daily/weekly), Days (list of day numbers), Time
- [ ] Add IsRecurring check to Event

### ☐ Next Instance Calculation
**File:** `Services/RecurringEventService.cs`

**Tasks:**
- [ ] Create RecurringEventService
- [ ] Add CalculateNextInstance(Event event) method
- [ ] For weekly: find next occurrence of specified day
- [ ] Set time to specified time
- [ ] Return new DateTime for next instance

### ☐ Reschedule After Activation
**Tasks:**
- [ ] After event activates, check if recurring
- [ ] Calculate next instance
- [ ] Update event's StartTime in memory
- [ ] Save updated schedule to file
- [ ] Webhook Laravel with updated event (optional)
- [ ] Log rescheduled event

---

## Phase 7: Error Handling & Recovery

### ☐ Startup Recovery
**Tasks:**
- [ ] On startup, check if schedule file exists
- [ ] If not found, create empty schedule
- [ ] Check for events that should have activated while offline
- [ ] Log missed events (don't activate them)
- [ ] Continue with next scheduled event

### ☐ Restart Failure Handling
**Tasks:**
- [ ] If restart fails, log error
- [ ] Retry restart (max 3 attempts)
- [ ] If all retries fail, skip event
- [ ] Notify Laravel of failure (webhook)
- [ ] Continue with next event in schedule

### ☐ Network Failure Handling
**Tasks:**
- [ ] If Laravel webhook fails, log error
- [ ] Continue event activation (don't block)
- [ ] Retry webhook in background
- [ ] Event is still active even if webhook fails

### ☐ Configuration Errors
**Tasks:**
- [ ] Validate event data on schedule receive
- [ ] Check required fields are present
- [ ] Validate datetime formats
- [ ] Reject invalid schedules with clear error message
- [ ] Return HTTP 400 with validation errors

---

## Phase 8: Logging & Monitoring

### ☐ Comprehensive Logging
**Tasks:**
- [ ] Log all schedule updates from Laravel
- [ ] Log event scheduler checks ("Checking for events...")
- [ ] Log event activation start ("Activating event: X")
- [ ] Log smart restart state transitions
- [ ] Log all server messages sent
- [ ] Log restart attempts and results
- [ ] Log webhook attempts
- [ ] Log recurring event rescheduling
- [ ] Use structured logging with context

### ☐ Event History (Optional)
**Tasks:**
- [ ] Create EventHistory.json file
- [ ] Log: event ID, name, activation time, success/failure
- [ ] Add endpoint: GET /api/Events/history
- [ ] Return last 50 activated events
- [ ] Useful for debugging

---

## Phase 9: Testing

### ☐ Unit Tests
**Tasks:**
- [ ] Test Event model serialization/deserialization
- [ ] Test EventSchedule save/load
- [ ] Test recurring pattern calculation
- [ ] Test smart restart state machine
- [ ] Test player count detection logic
- [ ] Test timeout logic (10 minutes)

### ☐ Integration Tests
**Tasks:**
- [ ] Test POST /api/Events/schedule endpoint
- [ ] Test GET /api/Events/current endpoint
- [ ] Test POST /api/Events/{id}/activate endpoint
- [ ] Test complete activation flow (mock server process)
- [ ] Test Laravel webhook delivery

### ☐ Manual Testing
**Tasks:**
- [ ] Create test event in Laravel for 2 minutes from now
- [ ] Verify C# receives schedule
- [ ] Watch for 5-minute countdown (should start immediately for 2min event)
- [ ] Join server and verify messages appear
- [ ] Wait for restart
- [ ] Verify server restarts with new config
- [ ] Check Laravel receives webhook
- [ ] Verify event marked active in Laravel

---

## Phase 10: Documentation

### ☐ Code Documentation
**Tasks:**
- [ ] Add XML comments to all public methods
- [ ] Document Event model properties
- [ ] Document API endpoint parameters
- [ ] Document smart restart states

### ☐ Configuration Guide
**Tasks:**
- [ ] Document event schedule JSON format
- [ ] Document where schedule file is stored
- [ ] Document how to manually trigger events
- [ ] Document logging locations

---

## Implementation Order (Recommended)

1. **Phase 1** - Models & Data (foundation)
2. **Phase 2** - API Endpoints (receive schedules from Laravel)
3. **Phase 6** - Recurring Events (logic needed before phase 4)
4. **Phase 3** - Smart Restart (core feature)
5. **Phase 4** - Event Scheduler (ties it together)
6. **Phase 5** - Laravel Integration (webhooks)
7. **Phase 7** - Error Handling (robustness)
8. **Phase 8** - Logging (observability)
9. **Phase 9** - Testing (verification)
10. **Phase 10** - Documentation (maintenance)

---

## Key Decision Points

### 1. Server Message Command
**CONFIRMED:** Command format is `/message [new message]`
- Example: `/message Server restarting in 5 minutes`
- Write to server process stdin
- Update SendServerMessage() to use this format

### 2. Timer Intervals
**Current Plan:**
- Event scheduler: Check every 30 seconds
- Countdown warnings: Every 1 minute
- Smart restart checks: Every 30-60 seconds during Pending

**TODO:** Adjust if needed based on testing

### 3. Timeout Duration
**Current Plan:** 10 minutes max wait for lobby

**TODO:** Make configurable? User feedback?

### 4. Handling Overlapping Events
**Current Plan:** First come, first served (by start time)

**TODO:** Add priority system? Queue events?

---

## Dependencies

**Requires from existing codebase:**
- Server process management (start/stop/restart)
- Server configuration API
- Track rotation deployment
- Player tracking system
- Existing webhook infrastructure

**New NuGet Packages Needed:**
- None (use existing System.Text.Json, etc.)

---

## Testing Checklist

Before marking feature complete:

- [ ] Event created in Laravel appears in C# GET endpoints
- [ ] Manual activation from Laravel works
- [ ] Automatic activation at scheduled time works
- [ ] 5-minute countdown messages appear in-game
- [ ] Server waits for lobby when players online
- [ ] Server restarts immediately when no players
- [ ] 10-minute timeout forces restart
- [ ] Server config (name, message) updates correctly
- [ ] Track rotation deploys correctly
- [ ] Laravel receives webhook on activation
- [ ] Recurring events reschedule after activation
- [ ] Schedule persists across C# service restarts
- [ ] Errors logged appropriately
- [ ] Multiple events in schedule work

---

**Total Tasks:** ~80-90 individual tasks across 10 phases

**Estimated Effort:**
- Phase 1-2: 4-6 hours
- Phase 3-4: 8-10 hours (smart restart is complex)
- Phase 5-6: 4-6 hours
- Phase 7-10: 6-8 hours
- **Total: 22-30 hours**

Good luck! 🚀
