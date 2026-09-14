# HomeOps Ideation Guidance

Generate useful new ideas for HomeOps, with particular emphasis on information that could improve an AI backend's understanding of the world around a robot.

The system can expose data through APIs that other software or AI agents consume.

Ideation should therefore focus primarily on new data sources, useful derived measurements, context, and lightweight capabilities that make the API more informative.

## Core direction

Look for information that could help an AI agent answer questions such as:

- What is happening around me?
- What conditions should I expect soon?
- Is now a good time to perform a particular activity?
- Has something unusual changed?
- What contextual information is relevant to planning or decision-making?
- Can several simple measurements be combined into a more useful higher-level signal?

The data does not need to control a robot directly. Context that improves reasoning is valuable.

## Prefer

Favor ideas such as:

- Public internet data sources
- Environmental measurements
- Weather and forecast data
- Air quality
- UV levels
- Solar conditions
- Sunrise, sunset, daylight, and twilight
- Moon or astronomical information when it has a plausible use
- Electricity prices
- Grid conditions
- Traffic conditions
- Road conditions
- Public transport information
- Public warnings and alerts
- Local environmental observations
- Water or marine conditions
- Geomagnetic or space-weather data
- Calendar-derived context
- Seasonal information
- Location-dependent contextual data
- Derived metrics combining several existing data sources
- Trends, deltas, anomalies, or forecasts derived from existing measurements
- Simple classifications that are easier for an AI backend to consume than raw data

Examples of useful derived concepts might include:

- "good conditions for being outdoors"
- "risk of slippery roads"
- "rapid weather deterioration"
- "unusually high electricity price"
- "daylight remaining"
- "environmental conditions significantly different from normal"

These are examples, not a fixed feature list.

## Data-source qualities

Prefer sources that are:

- Publicly accessible
- Free or inexpensive
- Documented
- Stable enough for hobby or personal infrastructure
- Reasonably easy to query
- Legal and appropriate to consume automatically
- Useful even when refreshed relatively infrequently

Prefer APIs or structured sources over scraping websites when practical.

An idea does not need to identify the final provider if several reasonable providers exist. Investigation can select the best source later.

## Derived information

Do not limit ideas to adding more external APIs.

Consider whether existing HomeOps data can be combined into new useful measurements.

A simple derived signal may be more valuable to an AI backend than another raw endpoint.

Prefer derived information that:

- Has a clear interpretation
- Can be calculated deterministically
- Has an obvious reason for existing
- Reduces reasoning work for downstream consumers without hiding important source data

## API mindset

New capabilities should generally be exposable through a simple API.

Prefer:

- Small, understandable resources
- Clear timestamps and freshness information
- Source attribution where relevant
- Data that can be cached
- Predictable structured responses

Avoid designing an elaborate API architecture during ideation. The issue should describe the useful capability; implementation details can be determined later.

## Avoid

Do not propose:

- Integrations whose usefulness to HomeOps or an AI backend is unclear
- Data sources that mainly exist as curiosities with no plausible contextual value
- Expensive commercial APIs unless the value is exceptional
- Sources requiring complicated authentication for little benefit
- Fragile scraping when reasonable APIs exist
- Generic refactoring or cleanup without a concrete capability attached
- Large platform redesigns
- Features that duplicate existing HomeOps functionality without a meaningful improvement
- Generic "AI features" where the AI could already derive the same answer easily from existing exposed data

Do not propose ideas already represented by open issues.

## Scope

Prefer one clearly useful capability per idea.

A good issue should normally describe:

- What information or derived measurement would be added
- Why it could be useful
- Roughly where the information could come from
- How an API consumer might use it

Do not overspecify implementation.

If no sufficiently useful and novel idea is found, return no idea rather than creating filler.
