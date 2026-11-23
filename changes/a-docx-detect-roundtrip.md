- **Word documents are parsed again**: an uploaded or crawled .docx (and .odt) failed in its parse job with "required
  property missing" and the job went dead. The fetch job stored the document's detect output without the page count a
  Word document lacks, and the parse job could not read it back. Stored stage outputs now read a left-out value as
  empty; what is stored, and its hash, are unchanged.
