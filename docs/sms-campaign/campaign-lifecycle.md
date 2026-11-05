# Campaign lifecycle

## Current flow

```mermaid
stateDiagram-v2
    [*] --> Draft: Operator creates campaign
    Draft --> Draft: Operator edits template or deadline
    Draft --> Active: Operator confirms Start sending
    Active --> Active: Background worker claims and concurrently sends pending messages
    Active --> Closed: Optional reply deadline passes
```

```mermaid
sequenceDiagram
    actor Operator
    participant UI as React UI
    participant API as ASP.NET API
    participant DB as SQL Server
    participant Worker as Dispatch worker
    participant SMS as Farapayamak

    Operator->>UI: Create campaign and choose recipients
    UI->>API: POST /api/campaigns
    API->>DB: Save Draft campaign and rendered Pending messages
    API-->>UI: Campaign is ready; no external send
    Operator->>UI: Review or edit campaign
    UI->>API: PATCH /api/campaigns/{id}
    API->>DB: Re-render messages and reset unsent attempts atomically
    Operator->>UI: Confirm Start sending
    UI->>API: POST /api/campaigns/{id}/start
    API->>DB: Atomically change Draft to Active
    Worker->>DB: Select only active-campaign messages and mark batch Processing
    par Up to configured SendConcurrency
        Worker->>SMS: Form-encoded SendSMS request
        SMS-->>Worker: Provider status and reference
        Worker->>DB: Save Sent, Pending retry, or Failed in an independent scope
    end
```

## Preconditions and guards

- A campaign must have at least one valid recipient.
- Template fields must be known before persistence.
- A deadline, when supplied, must be in the future.
- A campaign with a sent or processing message cannot be edited.
- Starting is an atomic transition allowed only from `Draft`.
- Provider rejection does not mark the message as sent.
- Recipients may be all people, explicitly selected people, or people whose latest inbound result matches one or more selected statuses.
- Latest-result selection prevents an earlier invalid reply from overriding a later successful reply.

## Failure paths

- Transport and provider errors are stored on the outbound message.
- Failed messages can be retried from the delivery-progress page.
- Filtered or invalid provider content remains failed and requires editing before a new start.
- A claimed batch is persisted as `Processing` before concurrent provider calls start. Startup recovery returns interrupted `Processing` rows to `Pending`.
- Each concurrent send uses its own database context. `SendConcurrency` is bounded to `1..20` and defaults to `5`.

## Known gaps

- Provider filtering rules are external and no complete machine-readable blocked-word list is available.
- The application does not yet provide delivery-status polling for outbound provider references.
- Dispatch claiming assumes one active application instance. Running multiple application instances would require a durable claim token or database-locking strategy.
