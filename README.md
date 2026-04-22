# AgentRp

AgentRp is a stateful narrative chat workspace for building more consistent roleplay and story scenes, especially when you want to work with local, smaller, or limited-reasoning models. It is both a functional tool and a practical example of how to move narrative consistency out of fragile prompt-only memory and into the app itself.

## What makes it different

Most chat tools hand the whole problem to the model. AgentRp does not. It keeps explicit story state outside the model so the app can help maintain continuity instead of hoping the model remembers everything on its own.

The scene tracks who is present, which location is active, and which important items are in play. Character state separates stable general appearance from current in-scene appearance, so the system can distinguish "what this character usually looks like" from "what they look like right now in this moment." That current appearance can include visible body language, positioning, clothing, and similar details when the transcript supports it.

AI scene posts are also not single-shot. Guided and Automatic generation run through visible stages so the app can resolve current appearance, plan the next beat, and then write the final prose. Edits do not overwrite history either; they create branches, which makes experimentation and revision safer.

The chat can also compress transcript history into snapshots that become reusable narrative memory. On top of that, entity drafting and field guidance help shape cleaner structured context for characters, locations, items, facts, and timeline entries.

## Goal

The goal of AgentRp is to help local, smaller, and limited-reasoning models produce more consistent narratives over time. The approach is not "make the model smarter." The approach is to give the model better structure, better memory, and better planning so it has less room to drift.

That also makes this repository a learning resource. If you are exploring how to build better small-model experiences, AgentRp shows practical orchestration patterns you can reuse in your own work.

## How it works

Story entities and scene metadata are stored explicitly instead of being left entirely inside prompt text. The active branch determines the working transcript, so revisions can fork without destroying earlier versions. Snapshot drafts can turn a stretch of chat into structured facts and timeline entries, which gives the system a cleaner memory layer than a growing raw transcript.

Before the app writes an AI-generated scene message, it can resolve branch-local current appearance for the active scene cast. A planner stage then decides the next message's intent, goals, guardrails, and required beats. After that, a prose stage writes the final message from the plan. Where structured outputs make sense, the app uses them so weaker models have less room to wander.

At a high level, the implementation is a Blazor front end with SQL-backed persisted state, an Aspire app host, and OpenAI-compatible chat endpoints.

## Why this helps small models

Small models do better when they do not have to infer or remember everything at once. AgentRp reduces dependence on implicit memory, lowers the need for multi-step internal reasoning, and keeps prompt entropy down by curating state before it reaches the model.

When the narrative still starts to drift, the system is easier to recover because state is editable, branches preserve alternatives, and snapshots can re-anchor the story in structured form.

## Getting started

### Prerequisites

- Docker Desktop or another Docker runtime
- .NET 10 SDK

### Start the app

AgentRp is launched through the Aspire app host. Aspire will start the web app and the SQL container for you.

If you want to use hosted OpenAI, configure the AppHost secrets like this:

```bash
dotnet user-secrets --project AgentRp.AppHost set "Parameters:openai-api-key" "YOUR_OPENAI_API_KEY"
dotnet user-secrets --project AgentRp.AppHost set "Parameters:openai-model" "gpt-5.4"
```

If you want to use a local or custom endpoint, configure the custom parameters instead. This is meant for an OpenAI-compatible local endpoint such as LM Studio or an Ollama-compatible gateway.

```bash
dotnet user-secrets --project AgentRp.AppHost set "Parameters:custom-name" "Local"
dotnet user-secrets --project AgentRp.AppHost set "Parameters:custom-endpoint" "http://localhost:1234/v1"
dotnet user-secrets --project AgentRp.AppHost set "Parameters:custom-api-key" "optional-if-your-endpoint-needs-it"
```

Then run the Aspire host:

```bash
dotnet run --project AgentRp.AppHost
```

One important note for local endpoints: the current AppHost exposes the custom endpoint, name, and API key, but not a separate custom model parameter. If your local endpoint requires a specific model name instead of accepting the current custom default, update the custom `TextModel` value in [AgentRp/appsettings.json](/mnt/w/AgentRp/AgentRp/appsettings.json) before you run the app.

### First run

1. Create a chat.
2. Pick a speaker in the sidebar.
3. Try `Direct` to post your own text exactly as written.
4. Try `Guided` to give the app planning guidance for the next beat.
5. Try `Automatic` to let the app generate the next message without using the textbox.
6. Create a snapshot to compress part of the transcript into structured memory.
7. Edit a message into a branch so you can explore an alternate continuation without overwriting history.

## What to look at if you want to learn from it

If you want to study the repo as a design example, pay attention to the explicit world state, the branch-based revision model, and the staged generation flow. Those choices do a lot of the heavy lifting for consistency.

It is also worth looking at how the app handles structured responses for entity drafting, snapshots, planning, and appearance resolution. A big part of the design is using application architecture to compensate for weaker models instead of treating the model as the entire system.

## Current status

This system is still actively being designed and refined, and the current architecture is intentionally willing to change when a better foundation becomes clear. The product name in this README is **AgentRp**, while the current repo and project names are still `AgentRp`, `AgentRp.AppHost`, and `AgentRp.ServiceDefaults`.
