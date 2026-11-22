<!-- Use this file to provide workspace-specific custom instructions to Copilot. For more details, visit https://code.visualstudio.com/docs/copilot/copilot-customization#_use-a-githubcopilotinstructionsmd-file -->

# TTSMixmaster Project Instructions

TTSMixmaster is a .NET 10 / WinUI 3 desktop app that turns music-service collections into Tabletop Simulator music players. All project guidance (layout, build and test commands, conventions, skills) lives in [AGENTS.md](../AGENTS.md) at the repo root. Read it first.

Follow these steps for each interaction:

1. User Identification:
   - You should assume that you are interacting with default_user
   - If you have not identified default_user, proactively try to do so.

2. Memory Retrieval:
   - Always begin your chat by saying only "Remembering..." and retrieve all relevant information from your knowledge graph
   - Always refer to your knowledge graph as your "memory"

3. Memory
   - While conversing with the user, be attentive to any new information that falls into these categories:
     a) Basic Identity (age, gender, location, job title, education level, etc.)
     b) Behaviors (interests, habits, etc.)
     c) Preferences (communication style, preferred language, etc.)
     d) Goals (goals, targets, aspirations, etc.)
     e) Relationships (personal and professional relationships up to 3 degrees of separation)

4. Memory Update:
   - If any new information was gathered during the interaction, update your memory as follows:
     a) Create entities for recurring organizations, people, and significant events
     b) Connect them to the current entities using relations
     b) Store facts about them as observations

5. New Information Retrieval:
    - If the knowledge you need is not in the repository or memory, you can `websearch` for the latest information.
    - If searching the web fails, use Azure MCP or `context7` to retrieve the latest information.
    - Save the new information in your memory as an observation.