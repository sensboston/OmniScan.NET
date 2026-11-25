---
layout: default
title: Online services
---

# Online services: turning photos upright and describing scans

OmniScan works fully offline. With an API key of an online AI service it can also:

- **Turn photos upright.** A print put on the glass sideways or upside down is turned the right way
  after it is saved, even when there is no text on it to tell which way is up.
- **Describe photos and PDFs** with a short line and a few words to find them by, for example
  `Massachusetts ID card (Mass ID, driver's license, identity card)` or
  `Electric bill, October 2026 (electricity, utility bill)`.

Both are off until you enter a key and tick them in **Settings → AI**. The key is checked when you
paste it; it is stored encrypted for your Windows user (DPAPI) in `OmniScan.ini` and cannot be read by
another user or on another computer.

## Where the descriptions go

| | Photos (JPEG) | PDFs | Other images |
|---|---|---|---|
| `descript.ion` in the folder (shown and searched by Total Commander and Far Manager) | ✓ | ✓ | ✓ |
| File properties: *Subject*, *Comments* (shown by Explorer, indexed by Windows Search) | ✓ | — | — |
| The document's title (shown by PDF viewers on their tab or title bar) | — | ✓ | — |

To find a scan: in Far Manager, search the `Scans` folder for text (`Alt+F7`, with hidden files shown:
`descript.ion` is hidden, as Total Commander makes it); in Explorer, the photo's comments are searchable.
File names are never changed.

Descriptions are written in the language of the program and never contain numbers of accounts, cards
or documents, addresses, phone numbers or dates of birth: they end up in plain text next to the file.

## What is sent, and what is not

| Use | What leaves your computer |
|---|---|
| Turning a photo upright | 4 small copies of the photo (384 pixels on the long side), turned 4 ways; when the answer is "turn it", a second time in another order, to confirm |
| Describing a photo | one small copy (384 pixels), in the same request |
| Describing a PDF | its first page, 1280 pixels on the long side |

Nothing else is sent: not the other PDF pages, not the full-size scans, not the file names, not
anything about your computer. PDF pages are never turned by a service: they are turned upright on
your computer, by the text recognition built into Windows.

**Think before turning "Describe photos and PDFs" on** if you scan papers that strangers must not see:
the first page of each PDF is sent to the service you chose.

## Services

The service is recognized from the key. Tested in November 2026:

| Service | Key starts with | Model chosen | Notes |
|---|---|---|---|
| [OpenAI](https://platform.openai.com/api-keys) | `sk-` | `gpt-6-luna` | Fast (about a second per photo), costs a fraction of a cent per photo. |
| [Google AI Studio](https://aistudio.google.com/apikey) | `AIza…` or `AQ.` | `gemini-3.5-flash-lite` | The most accurate in our tests, and there is a free tier. Slower (1–13 s per photo). |
| [DeepSeek](https://platform.deepseek.com/api_keys) | `sk-` (32 hex digits) | `deepseek-flash` | Accurate descriptions; once turned a photo the wrong way in our tests. |
| Anthropic, xAI, OpenRouter | `sk-ant-`, `xai-`, `sk-or-` | chosen from the key's model list | Supported, not tested yet. |

How the tested models did on our set of scans (24 turned photos, and one hard case: a spacecraft
lying on its side where only an upside-down sign shows which way is up):

| Model | Photos turned right (of 24) | The hard case (of 4) |
|---|---|---|
| gemini-3.5-flash-lite | 24 | 4 |
| deepseek-flash | 23 | 0–2, once a wrong turn |
| gpt-6-luna | 23 | 1 (otherwise "not sure": left as it was) |
| gpt-6.1-terra | 24 | 4 |

A photo is turned only when two answers with the copies in a different order agree; otherwise it
is left as it was.

### Privacy: what each service may do with what you send

This depends on the service and on your plan, and their terms change; read them before you choose.
In short, at the time of writing:

- **OpenAI (paid API):** data sent through the API is not used to train their models by default;
  it may be kept for a limited time for abuse monitoring.
- **Google AI Studio, free tier:** what you send **may be used to improve Google's products and may be
  seen by human reviewers.** Fine for postcards and holiday photos; think twice for documents and
  photos of people. The paid tier does not use your data that way.
- **DeepSeek:** the service is run from China, under Chinese law, and its privacy policy allows using
  what you send to improve the service. We would not send personal documents to it.
- **OpenRouter** passes requests on to models of other companies, under their terms.

OmniScan shows a short version of this in **Settings → AI** for the service of the key you entered.

## Choosing the model by hand

The model is chosen for the service of your key. To use another one, set it in `OmniScan.ini`
(next to `OmniScan.exe`):

```ini
[Assist]
; Model: the model to use first; empty = chosen by the program for the key's service.
Model = gpt-6.1-terra
```

The file lists the tested models, and a commented-out line for `gpt-6.1-terra`: it turns more photos
right than `gpt-6-luna` (also those where only a sign shows which way is up), at about ten times the price.

When a model is busy (common for new models on free tiers), the request goes to the next model of
the service and the busy one is left alone for two minutes; a model that is no longer available to
your key is skipped for half an hour.
