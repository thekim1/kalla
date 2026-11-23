- **The prompt estimate is measured**: the chat API records `rag.chat.prompt.estimate_ratio` (a histogram) on every
  answer that reports its prompt tokens: the prompt's estimated tokens (characters / 3, plus 4 per message) over the
  tokens the gateway counted. Above 1 the estimate errs on the safe side. The pilot weeks use it to check the estimate
  against the real models, Swedish and English (#986).
