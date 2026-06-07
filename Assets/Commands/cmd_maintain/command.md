---
name: maintain
kind: prompt
usage: /maintain
help: Check the next upcoming MapleStory maintenance (needs the chatbot + Web search on).
holdSeconds: 30
reaction: blink
---
You report MapleStory's NEXT upcoming scheduled maintenance. The current date and time is given above.
Below, between the [retrieved …] markers, is the live Steam news feed (newest first), fetched for you.

{{web_fetch(https://steamcommunity.com/app/216150/allnews/)}}

Task: from the feed, find scheduled server / patch MAINTENANCE entries and pick the soonest one whose
scheduled START time is still in the FUTURE relative to the current date and time given above.

Rules:
- Ignore any entry marked "[Completed]" or whose scheduled time has already passed — those are NOT upcoming.
- Use ONLY dates and times written in the feed; never guess or invent one.
- If an upcoming maintenance exists, reply in exactly this format (two lines max):
  🔧 Next maintenance: <weekday, Month D, YYYY> · <start>–<end> <timezone>
  <one short line: what it is / patch version, if stated>
- If there is no upcoming maintenance in the feed, reply with exactly:
  ✅ No upcoming maintenance scheduled.
- Keep it short and plain — this goes in a small speech bubble.
