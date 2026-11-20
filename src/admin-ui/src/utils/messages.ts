/**
 * The UI texts for the admin API's error codes (admin design §4.2), `{param}` filled from the problem's `params`.
 * Every code in contracts/admin/v1/error-codes.json has an entry; messages.spec.ts checks it. English now; a Swedish
 * table with the same keys comes later as a translation (Decisions, "Admin UI in English").
 */
export const ERROR_MESSAGES: Record<string, string> = {
  invalid_value: 'The value is not valid. Check the field.',
  platform_rule: 'The change breaks a platform rule ({constraint}). Check the highlighted fields.',
  not_found: 'It no longer exists, or you do not have access to it. Reload the page.',
  concurrent_change: 'Someone else changed this while you were working. Reload the page; your changes stay in the form.',
  already_exists: 'Something with the same name or key already exists. Choose another one.',
  in_use: 'The change conflicts with something that refers to it. Remove that reference first.',
  config_busy: 'Another change is being saved right now. Try again in a moment.',
  sync_off: 'The sync with the AI gateway is not set up. Contact the platform team.',
  sync_failed: 'The sync with the AI gateway failed ({error}). The organisation fetched last time is kept.',
  model_residency: 'The model {model} cannot be used at the level’s ceiling {ceiling}.',
  model_not_listed: 'The AI gateway does not offer {model} at {ceiling} right now. Try again later.',
  gateway_unavailable: 'The AI gateway could not be reached to check the model. Try again.',
  gateway_key_missing: 'No AI gateway key is set up for {ceiling}, so {model} cannot be checked there. Contact the platform administrator.',
  policy_rule: 'The data protection setting breaks a rule: {rule}',
  not_in_scope: 'That department is not one of yours.',
  no_embedding_profile: 'There is no default embedding profile. Contact the platform team.',
  collection_in_use: 'The knowledge base is still in use. Remove it from the assistants and remove its sources first.',
  collection_level_conflict: 'The knowledge base cannot be lowered to {level}: a source has a higher information class.',
  assistant_below_collection: 'The assistant’s information class is lower than the knowledge base {collection} ({level}). Raise the assistant’s class or remove the knowledge base.',
  retrieval_settings_invalid: 'The search setting {field} must be {expected}.',
  site_profile_invalid: 'The site profile field {field} is not valid: {problem}',
  site_profile_version_applied: 'That version is already in use.',
  no_department: 'There are no departments yet. Run the organisation sync first.',
  source_above_collection: 'A source cannot have a higher information class than its knowledge base {collection} ({level}).',
  source_settings_invalid: 'The source setting {field} is not valid: {problem}',
  impact_not_confirmed: 'The change affects which documents the source holds. Check the impact and confirm.',
  template_unsupported: 'There is no processing for the source type {type} that the running workers support.',
  admin_only: 'Only a platform administrator can change this.',
  run_not_active: 'The run has already ended ({state}).',
  reason_too_short: 'The reason must be at least {min} characters.',
  deletion_superseded: 'The deletion proposal no longer applies. Open the source’s latest run.',
  pii_not_flagged: 'The document is not flagged for personal data.',
  not_an_upload_source: 'Files can only be uploaded to a source of the type Files.',
  upload_format_deferred: 'Files of the type {extension} cannot be uploaded yet. Save the message as PDF.',
  upload_type_refused: 'Files of the type {extension} cannot be uploaded.',
  upload_too_large: 'The file is larger than {mb} MB.',
  upload_file_exists: 'File {n} of the batch has already been sent.',
  upload_file_missing: 'File {n} has not been uploaded to the batch.',
  upload_batch_too_large: 'A batch holds 1 to 100 files and at most 500 MB.',
  source_paused: 'The source is paused. Resume it first.',
  run_not_continuable: 'Only the latest crawl run that ended with pages left or failed can be continued. Use Sync now.',
  slug_taken: 'The short name {slug} is already in use. Try {suggestion}.',
  upload_batch_committed: 'The batch has already been saved with other files.',
  key_not_rotatable: 'Only an active key that has not already been replaced can be rotated.',
  client_managed_by_deployment: 'The deployment manages this app’s key. To replace it, deploy a new one.',
  key_revoked: 'The key is revoked.',
  key_secret_unavailable: 'The key’s secret cannot be shown. Rotate the key to get one that can be shown.',
  key_scope_invalid: 'A key can only be limited to assistants and knowledge bases in the team’s department.',
  key_scope_required: 'Limit the key to at least one assistant or knowledge base in your department.',
  keys_unavailable: 'Keys cannot be created right now. Contact the platform team.',
  no_evaluation_set: 'The knowledge base has no test questions yet.',
  question_not_human: 'Only questions written by people can be changed here.',
  question_edit_pending: 'An earlier change to the question is still being saved. Try again in a moment.',
  no_draft: 'There is no draft to publish.',
  draft_is_only_version: 'The draft is the assistant’s only version.',
  draft_base_changed: 'The draft is based on an older version than the published one. Confirm to publish it anyway.',
  assistant_contact_missing: 'Add a description and a contact before you publish.',
  gateway_key_invalid: 'The AI gateway key {key} cannot be used ({reason}). Choose an active key of this department.',
  gateway_key_residency: 'The AI gateway key {key} allows no residency within {ceiling}. Choose a key that may run at this information class.',
  gateway_key_model: 'The AI gateway key {key} does not allow every model this uses.',
  gateway_key_model_residency: 'The AI gateway key {key} reaches {model} only at {modelResidencies}, which the key does not allow (it allows {keyResidencies}).',
  gateway_key_embedding_residency: 'The AI gateway key {key} reaches {model}, the knowledge base’s embedding model, only at {modelResidencies}, which the key does not allow (it allows {keyResidencies}).',
  gateway_key_required: 'No AI gateway key is chosen. Choose one under Edit details: the assistant can’t be published without one, and until then its calls are charged to the platform’s owner team.',
  gateway_key_system_prompt: 'The AI gateway key {key} adds its team’s system prompt to every call, which changes the assistant’s prompts.',
  period_too_long: 'The period is too long: at most 400 days by day, or 36 months by month.',
  version_link_missing: 'The assistant refers to something that is missing: {link}.',
  too_many_collections: 'An assistant can use at most 20 knowledge bases.',
  collection_not_shared: 'A knowledge base belongs to another department and is not shared with yours.',
  topic_invalid: 'A topic is invalid. Use at most 20 topics per knowledge base, each in lower case letters, digits and hyphens.',
  collection_shared_in_use: 'Assistants of other departments use this knowledge base. They would lose it, so the sharing cannot be narrowed.',
  move_not_confirmed: 'What the move does has changed, or it was not confirmed. Check it again and confirm.',
  move_pipeline_department: 'Sources use a pipeline of the old department. Give them a platform pipeline or one of the new department first.',
  audience_above_ceiling: 'An app cannot show an assistant with the information class {level}.',
  client_stores_history: 'An assistant with the class Sensitive cannot be offered in an app that saves chat history.',
  query_steps_invalid: 'The setting {field} has an invalid value: use a whole number within the allowed range, or true/false for a switch.',
  general_knowledge_public_unconfirmed: 'General knowledge ({generalKnowledge}) on a public assistant is not recommended: the public may get general answers that are wrong for your organisation. Confirm to go ahead.',
  link_host_not_allowed: 'A link must be https to an allowed site, and at most 5 links.',
  settings_model_residency: 'The model {model} cannot be used for {setting}: it is not a {kind} model the AI gateway offers at {ceiling}.',
  crawl_politeness_invalid: 'The politeness setting {field} for {host} is not valid: {problem}',
  file_not_restorable: 'The file {name} cannot be brought back: it is not deleted, or its 14 days have passed.',
  profile_in_use: 'The embedding profile {profile} is already used by indexed content, so only its residency ceiling can change. A new model or size needs a profile migration.',
  profile_test_failed: 'The profile was not saved because the test embedding failed ({error}). Run Test to see what to fix.',
  prompt_text_invalid: 'The prompt text {field} cannot be saved: {problem}.',
  evaluation_model_unset: 'No model is chosen for creating test questions. A platform administrator chooses it under Settings.',
  version_not_restorable: 'Version {number} is the draft or the published version and cannot be restored.',
  retention_outside_policy: 'The information class {level} allows history {modes} and at most {maxDays} days.',
  pipeline_params_invalid: 'The setting {param} does not work for this processing: {message}',
  publication_not_ready: 'The plan is not ready. Wait a moment or make a new plan.',
  publication_stale: 'The processing has changed since the plan was made. Make a new plan.',
  version_not_rollbackable: 'Only an earlier published version can be restored.',
  test_answer_failed: 'The model could not answer the test question ({reason}). Try again.',
  test_assistant_unavailable: '{reason}',
  test_budget_exceeded: '{reason}',
  sign_in_not_configured: 'Sign-in is not set up yet. Contact the platform team.',
  setup_code_invalid: 'The setup code is not right. Check it in the output of the kalla-admin container and try again.',
  setup_attempts_exhausted: 'Too many setup code attempts. Wait a minute and try again.',
  setup_session_required: 'Enter the setup code first.',
  setup_test_refused: 'The test sign-in could not start ({reason}).',
  setup_test_required: 'Saving needs a passed test sign-in by a platform admin with exactly these values ({reason}).',
  search_unavailable: 'The search could not run right now. Try again in a moment.',
}

/**
 * UI texts for the worker's refusal and check codes (`Jobs.LastError`, command and preview results; worker #115,
 * #117, issue #116). Shown on runs, commands and previews; an unknown code falls back to `jobMessage`'s generic text.
 */
export const JOB_MESSAGES: Record<string, string> = {
  'site_profile.unknown': 'The site profile does not exist.',
  'site_profile.host_mismatch': 'The profile is for another website than the source.',
  'site_profile.version_unknown': 'The profile version does not exist.',
  'site_profile.version_conflict': 'The profile has changed since you opened it. Reload and try again.',
  'collection.unknown': 'The knowledge base does not exist.',
  'eval_question.unknown': 'The test question does not exist.',
  'eval_question.conflict': 'Someone else has changed the test question. Reload and try again.',
  'eval_question.retired': 'The test question has been removed and cannot be changed.',
  'eval_question.unknown_document': 'The expected document is not in the knowledge base.',
  'run.unknown': 'The run does not exist.',
  'deletion.not_awaiting': 'The run is not waiting for a deletion decision.',
  'source.unknown': 'The source does not exist.',
  'document.unknown': 'The document does not exist.',
  'document.deleted': 'The document has been removed.',
  'document.no_content': 'The document has no saved content to process again.',
  'source.paused': 'The source is paused. Resume it first.',
  'pipeline.unpublished': 'The source’s processing has no published version.',
  'reprocess.fetch_unsupported': 'This document cannot be fetched again; process the saved content again instead.',
  'command.invalid_payload': 'The command was malformed and did not run. Contact the platform team.',
  'preview.limit_documents': 'Too many documents for a preview. Choose fewer.',
  'preview.invalid_payload': 'The preview was requested incorrectly and did not run.',
  'preview.unknown_kind': 'This kind of preview does not exist.',
  'preview.unsupported': 'This preview is not available yet.',
  'preview.collection_unknown': 'The knowledge base does not exist.',
  'preview.source_unknown': 'The source does not exist.',
  'preview.document_unknown': 'The document does not exist.',
  'preview.upload_unknown': 'The upload no longer exists.',
  'parse.empty': 'No text was left after the page was read and cleaned. If the website builds its pages with JavaScript in the browser, the text is not in the page the platform reads: set the source to fetch pages with a browser (JavaScript).',
  'raw.missing': 'The fetched content no longer exists. Run the preview again.',
  'preview.deadline': 'The preview took too long and was stopped. Try with fewer documents.',
  'preflight.none_reachable': 'None of the addresses answered. Check the addresses and that the website can be reached.',
  'preflight.deadline': 'The check took too long and was stopped. Try again.',
  'pages.within_limit': 'The number of pages fits within the limit.',
  'crawl.limit_pages': 'The website has more pages than the limit; only the first ones are fetched.',
  'crawl.incremental': 'The run fetched only changed pages, so removed pages are found at the next full pass.',
  'crawl.continued': 'The run continued an earlier one, so removed pages are found at the next full pass.',
  'crawl.host_paused': 'The website answered with too many errors and was paused during the run.',
  'crawl.seeds_failed': 'One of the start addresses did not answer.',
  'crawl.seeds_unreachable': 'None of the start addresses could be reached.',
  'crawl.claims_pending': 'Not all pages found could be fetched in time.',
  'crawl.cancelled': 'The run was cancelled.',
  'sitemap.found': 'A sitemap was found.',
  'sitemap.none': 'No sitemap was found; pages are found through links. If the website has one elsewhere, enter its address under Sitemaps.',
  'robots.ok': 'The website’s robots.txt allows fetching.',
  'robots.disallow': 'The website’s robots.txt does not allow fetching the address. Change the address or ask the website’s owner for permission.',
  'robots.unreachable': 'The website’s robots.txt could not be read; fetching waits until it answers.',
  'robots.noindex': 'The page asks not to be indexed and is not fetched.',
  'sitemap.unavailable': 'The sitemap could not be fetched; pages are found through links. Check its address under Sitemaps.',
  'sitemap.invalid': 'The sitemap is malformed; pages are found through links. Check its address under Sitemaps.',
  'sitemap.too_large': 'The sitemap is too large; only part of it is used.',
  'sitemap.truncated': 'The sitemap was too long; only part of it is used.',
  'http.200': 'The address answers.',
  'http.301': 'The address redirects to another address.',
  'http.404': 'The page does not exist (404). Check the address.',
  'http.410': 'The page has been removed (410).',
  'http.5xx': 'The website answered with a server error. Try again later.',
  'http.moved_out_of_scope': 'The address redirects to a page outside the source.',
  'http.content_type': 'The address gives no content that can be read.',
  'http.body_limit': 'The page is too large to fetch.',
  'http.unexpected': 'The website answered in an unexpected way.',
  'tls.invalid_certificate': 'The website’s certificate is not valid; it is not fetched.',
  'redirect.downgrade': 'The address redirects from https to http, and an insecure redirect is never followed. Ask the website’s owner to redirect to https, or add the https address the redirect should lead to.',
  'redirect.invalid': 'The website redirects without a valid address to go to.',
  'redirect.limit': 'The address redirects too many times, or in a loop.',
  'redirect.out_of_scope': 'The address redirects for now to a page outside the source, often a sign-in page, and is skipped.',
  'redirect.cookie_required': 'The address redirects in a loop because the website wants to set a cookie, and cookies are never kept. Ask the website’s owner for an address that answers without a cookie.',
  'http.soft_404': 'The website answers this address with its page-not-found page instead of an error, so the last content found is kept. Check the address.',
  'http.challenge': 'The website answered with a bot check or a block page instead of the page. The last content found is kept and the website is asked again later; ask the website’s owner to let the crawler through.',
  'url.syntax': 'This is not a valid web address. Check how it is written.',
  'url.scheme': 'Only http and https addresses can be fetched.',
  'url.userinfo': 'An address with a user name or password in it is not fetched.',
  'url.host': 'The address has no valid website name.',
  'url.port': 'The address has an invalid port number.',
  'net.dns': 'The website’s name could not be found. Check the address; it is tried again later.',
  'net.connect': 'The website could not be reached. It is tried again later.',
  'net.timeout': 'The website did not answer in time. It is tried again later.',
  'net.body': 'The page broke off while it was being fetched. It is tried again later.',
  'fetch.abandoned': 'The page still did not answer after several attempts and is left until the next run.',
  'fetch.invalid_payload': 'The fetch was requested incorrectly and did not run. Contact the platform team.',
  'http.format_deferred': 'This file format is not supported yet.',
  'robots.noindex_ignored': 'The page asks not to be indexed, but the organisation’s own websites are always fetched.',
  'robots.nofollow_ignored': 'The page asks for its links not to be followed, but on the organisation’s own websites they are followed.',
  'scope.excluded': 'The address matches one of the source’s exclude rules.',
  'scope.not_included': 'The address matches none of the source’s include rules.',
  'scope.off_site': 'The address is on a different website from the source.',
  'scope.type': 'The source leaves out this kind of file.',
  'trap.endless_path': 'The address repeats parts of itself without end and is skipped.',
  'trap.query_variants': 'The page has too many variants that differ only in their parameters; the rest are skipped.',
  'trap.calendar': 'The address looks like an endless calendar and is skipped.',
  'crawl.host_blocked': 'The website is blocked on the platform and is not fetched.',
  'crawl.breaker': 'The website answered with too many errors, so fetching from it is paused for a while.',
  'crawl.retry_after': 'The website asked to wait before more pages are fetched.',
  'crawl.lastmod_skip': 'Not fetched: the sitemap says the page has not changed since it was last fetched.',
  'crawl.removal_check': 'The page is no longer in the sitemap, so it was fetched again to see if it still exists.',
  'sitemap.depth': 'The sitemap points to further sitemaps too many levels deep; the deepest ones are not read.',
  'sitemap.dtd': 'The sitemap contains a DTD and is not read, for security reasons.',
  'identity.folded': 'The page is the same as another address on the website and is kept under that address.',
  'egress.private_address': 'The address leads to an internal network address and is not fetched.',
  'egress.embedded_address': 'The address hides an internal network address and is not fetched.',
  'egress.scheme': 'Only http and https addresses can be fetched.',
  'egress.port': 'Only the standard web ports (80 and 443) and the ports the platform allows can be fetched.',
  'egress.userinfo': 'An address with a user name or password in it is not fetched.',
  'egress.blocked_host': 'The website is on the platform’s block list and is not fetched.',
  'egress.redirect_downgrade': 'The address redirects from https to http, and an insecure redirect is never followed. Ask the website’s owner to redirect to https, or add the https address the redirect should lead to.',
  'robots.missing': 'The website has no robots.txt; everything may be fetched.',
  'robots.ignored': 'robots.txt is ignored for this website, as a platform administrator decided.',
  'cms.sitevision': 'The website is built in Sitevision; the ready-made profile is used.',
  'cms.unknown': 'The publishing system is not recognised; a profile has to be made by hand.',
  'render.suggested': 'The page holds much less text without JavaScript than with it.',
  'render.compared': 'The page holds most of its text without JavaScript.',
  'render.empty':
    'The rendered page has little or no main text. It was probably ready before its content loaded: name the element that holds the loaded content under “Content is ready when this element has text”.',
  'render.not_configured': 'No browser for rendering pages is set up on the platform, so pages cannot be fetched with JavaScript. Contact the platform team.',
  'render.busy': 'All browsers that render pages were busy, so the page was not rendered this time.',
  'render.timeout': 'The page did not finish loading in the browser in time. The last content found is kept and the page is tried again later. If the page shows a loading text, name the element that holds the content.',
  'render.unavailable': 'The browser that renders pages could not be reached. The last content found is kept and the page is tried again later.',
  'render.bad_selector':
    'The browser does not accept the value of “Content is ready when this element has text” as a CSS selector, for example #content or main article (without < >). The last content found is kept; correct the setting and run again.',
  'render.selector_not_found':
    'The page loaded, but no element named in “Content is ready when this element has text” had text in time. The last content found is kept; check that the selector names an element the page really shows, or leave the field empty.',
  'render.mostly_empty':
    'At least half of the rendered pages had no main text. They were probably ready before their content loaded: name the element that holds the loaded content under “Content is ready when this element has text”.',
  'eval.unsupported': 'This kind of evaluation is not available yet.',
  'eval.invalid_payload': 'The evaluation was requested incorrectly and did not run. Contact the platform team.',
  'eval.no_questions': 'The knowledge base has no active test questions to evaluate with.',
  'eval.invalid_candidate_settings': 'The proposed search settings are not valid. Check the values and try again.',
  'eval.invalid_settings': 'The knowledge base’s search settings are not valid. Check them and try again.',
  'eval.embedding_residency': 'The embedding model cannot be used at the knowledge base’s level.',
  'eval.search_unavailable': 'The search was not available during the evaluation. Try again in a moment.',
  'eval.model_unset': 'No model is chosen for creating test questions. Choose one in the evaluation settings.',
  'eval.model_not_onprem': 'The model for test questions must run on own servers (locally in the AI gateway).',
  'eval.no_onprem_key': 'There is no key for local models in the AI gateway. Contact the platform team.',
  'eval.unknown_kind': 'This kind of test question job does not exist.',
  'clamav.limit': 'The file is too large for the malware scan and is not used.',
  'clamav.timeout': 'The malware scan took too long. It is tried again later.',
  'clamav.unavailable': 'The malware scan is not available right now. It is tried again later.',
  'change.noise': 'The page changed only in ways that do not affect its text, so nothing was processed again.',
  'rule.unaffected': 'The changed processing rule does not affect this document, so its content was kept.',
  'version.unaffected': 'The new processing version does not affect this document, so its content was kept.',
  'version.swapped_back': 'The document went back to an earlier processed version.',
  'execution.superseded': 'A newer processing of the same document finished first, so this one was dropped.',
  'pipeline.republished': 'The processing was published again while this document was being processed; the new version takes over.',
  'pipeline.refused': 'The processing version could not be used. Contact the platform team.',
  'plan.version_unknown': 'The version to publish or roll back to does not exist.',
  'document.concurrent_insert': 'The same page was fetched twice at the same time. It is tried again.',
  'job.exception': 'An unexpected error stopped the job. Contact the platform team if it happens again.',
  'node.fatal': 'A processing step failed. Contact the platform team if it happens again.',
  'node.transient': 'A processing step failed for now. It is tried again later.',
  'command.unknown_kind': 'This kind of command does not exist. Contact the platform team.',
  'run.cancelled': 'The run was cancelled.',
  'sync.invalid_payload': 'The sync was requested incorrectly and did not run. Contact the platform team.',
  'sync.unsupported': 'This kind of source cannot be synced this way.',
  'sync.run_in_progress': 'Another run of the source is still going; this one waits for it.',
  'parse.invalid_payload': 'The reading step was requested incorrectly and did not run. Contact the platform team.',
  'embed.invalid_payload': 'The embedding step was requested incorrectly and did not run. Contact the platform team.',
  'deletion.rejected': 'The proposed removals were rejected and the documents are kept.',
  'deletion.superseded': 'The proposal was replaced by a newer one from a later run.',
  'sample.too_small': 'Too few pages had a sitemap date to judge whether the dates can be trusted.',
  'embed.bad_vector': 'The AI gateway returned an unusable embedding. Contact the platform team if it happens again.',
  'embed.canary_mismatch': 'The embedding model gives different results than before, so embedding is paused. Contact the platform team.',
  'embed.profile_changed': 'The embedding profile was changed while this document was being embedded. It is embedded again with the new profile.',
  'embed.input_missing': 'The text to embed was not found. Process the document again.',
  'embed.input_rejected': 'The AI gateway refused the text to embed.',
  'embed.profile_unknown': 'The knowledge base’s embedding model does not exist. Contact the platform team.',
  'gateway.timeout': 'The AI gateway did not answer in time. It is tried again later.',
  'gateway.unreachable': 'The AI gateway could not be reached. It is tried again later.',
  'gateway.unavailable': 'The AI gateway is busy right now. It is tried again later.',
  'gateway.rate_limited': 'Too many calls to the AI gateway right now. It is tried again in a moment.',
  'gateway.budget_exhausted':
    'A budget in the AI gateway (the knowledge base’s key, its team’s or its department’s) is used up for now. It is tried again at the next hour.',
  'gateway.key_unavailable':
    'The knowledge base’s gateway key cannot be used: it is disabled, expired or revoked, or not assigned to the platform. It is tried again at the next hour. Choose another key or ask the gateway team.',
  'gateway.delegation_not_applied':
    'The AI gateway did not confirm that it ran the call as the knowledge base’s key, so the result was not used. Contact the platform team.',
  'gateway.residency_above_ceiling': 'The model runs at a location above what this level allows.',
  'gateway.forbidden': 'The AI gateway refused the call. Contact the platform team.',
  'eval.embedding_profile_unknown': 'The knowledge base’s embedding model does not exist. Contact the platform team.',
  'eval.embedding_unavailable': 'The questions could not be embedded during the evaluation. Try again in a moment.',
  'eval.model_not_at_ceiling': 'The chosen model cannot be used at the knowledge base’s level. Choose another model.',
  'eval.model_unknown': 'The AI gateway does not know the chosen model. Choose another model.',
  'eval.no_key': 'The platform has no AI gateway key for this level. Contact the platform team.',
  'eval.request_rejected': 'The AI gateway refused the request. Contact the platform team if it happens again.',
  'eval.stopped': 'The evaluation stopped because the AI gateway did not answer.',
  'parser.unavailable': 'The document reader is not available right now. It is tried again later.',
  'parser.not_configured': 'The document reader is not set up. Contact the platform team.',
  'parser.encrypted': 'The file is password-protected or encrypted and cannot be read.',
  'parser.too_large': 'The file is too large to read.',
  'parser.too_many_pages': 'The file has too many pages to read.',
  'parser.format_deferred': 'This file format is not supported yet.',
  'parser.unsupported_format': 'This file format cannot be read.',
  'parser.limit_exceeded': 'The file goes beyond the reader’s safety limits and is not read.',
  'parser.timeout': 'Reading the file took too long.',
  'parser.worker_crashed': 'The document reader stopped while reading the file.',
  'parser.resource_limit': 'Reading the file needed more memory than allowed.',
  'parser.ocr_failed': 'Text recognition (OCR) failed. It is tried again later.',
  'parser.conversion_failed': 'The file could not be converted for reading. It is tried again later.',
  'parser.ner_not_ready': 'The name check is not ready yet. It is tried again in a moment.',
  'pii.flagged': 'Personal data was found, so the document is held and not used until an admin clears it.',
  'pii.ner_pending': 'The document is held until the check for names has run.',
  'upload.too_large': 'The file is too large.',
  'upload.manifest_missing': 'The upload’s file list is missing. Upload the files again.',
  'upload.blob_missing': 'The uploaded file is missing. Upload it again.',
  'upload.blob_damaged': 'The uploaded file is damaged. Upload it again.',
  'upload.hash_mismatch': 'The uploaded file does not match what was sent. Upload it again.',
  'upload.deleted': 'The file was removed from the upload.',
  'upload.duplicate': 'The same file is already in the source.',
  'upload.unknown_slot': 'The file to replace does not exist in the source.',
  'upload.slot_in_use': 'Another upload is already changing this file.',
  'upload.format_deferred': 'This file format is not supported yet.',
  'upload.macro_enabled': 'Files with macros are not accepted.',
  'upload.archive': 'Archives (such as zip files) are not accepted. Upload the files inside instead.',
  'upload.type_unknown': 'The file type could not be recognised.',
  'upload.type_mismatch': 'The file’s content does not match its file type.',
}

/** Codes the worker builds at run time (an HTTP status or the service's own code appended): the longest prefix wins. */
export const JOB_MESSAGE_PREFIXES: Readonly<Record<string, string>> = {
  'gateway.http_': 'The AI gateway answered with a server error. It is tried again later.',
  'parser.http_': 'The document reader answered with an error. It is tried again later.',
  'parser.ner_': 'The name check is busy or not available. It is tried again in a moment.',
  'gateway.': 'The AI gateway refused the call. Contact the platform team.',
  'parser.': 'The document reader could not read the file.',
}

/** The text of a worker code, or a generic text that still names the code for support. */
export function jobMessage(code: string): string {
  const exact = JOB_MESSAGES[code]
  if (exact) return exact
  const prefix = Object.keys(JOB_MESSAGE_PREFIXES).filter((p) => code.startsWith(p)).sort((a, b) => b.length - a.length)[0]
  return prefix
    ? `${JOB_MESSAGE_PREFIXES[prefix]} (${code})`
    : `Something went wrong in the processing (${code}). Contact the platform team if it happens again.`
}

/**
 * The outcome of an embedding profile test (admin design §4.5, `POST /api/embedding-profiles/{id}/test`) in plain words,
 * with the fix on the AI gateway side. `{model}`, `{residency}`, `{dimensions}` and `{expected}` are filled by
 * `embeddingTestMessage`.
 */
export const EMBEDDING_TEST_MESSAGES: Record<string, string> = {
  ok: 'The test worked: {model} answered with {dimensions} dimensions at the residency {residency}.',
  model_not_allowed: 'The AI gateway key of this residency may not use {model}. In the gateway, add the model to the key\'s allowed models, or choose a model the key may use.',
  model_not_found: 'The AI gateway has no model called {model}. Check the name against the gateway\'s model list.',
  max_residency_not_met: 'The AI gateway has no provider for {model} at or below the residency {residency}. In the gateway, add a deployment at that residency, or raise the profile\'s ceiling.',
  bad_vector: 'The AI gateway returned {dimensions} dimensions, but the profile has {expected}. Turn on Send dimensions if the model can shorten its vectors, or set the profile\'s size to {dimensions}.',
  residency_refused: 'The AI gateway answered from a residency ({residency}) above the profile\'s ceiling. Check the routes of {model} in the gateway.',
  canary_mismatch: 'The model now gives different embeddings than the ones already indexed, so the change is refused. Check which model the gateway serves under {model}.',
  gateway_not_configured: 'No AI gateway is configured for the admin service, so the profile cannot be tested.',
  gateway_key_missing: 'The admin service may not call the AI gateway at the residency {residency}. Check its credential key (Gateway:Key) in the deployment and the key’s allowed residencies in the gateway.',
  gateway_unavailable: 'The AI gateway could not be reached or failed. Try again in a moment.',
}

/** A test outcome as plain words for the values tested (`profile`); an unknown gateway code is named so support can find it. */
export function embeddingTestMessage(
  result: { ok: boolean; dimensions?: number | null; residency?: string | null; error?: string | null },
  profile: { gatewayModel: string; dimensions: number; maxResidency: string },
): string {
  const key = result.ok ? 'ok' : (result.error ?? 'error')
  const template = EMBEDDING_TEST_MESSAGES[key] ?? `The test embedding failed (${key}). Contact the platform team if it happens again.`
  return formatMessage(template, {
    model: profile.gatewayModel,
    residency: result.residency ?? profile.maxResidency,
    dimensions: result.dimensions ?? undefined,
    expected: profile.dimensions,
  })
}

/** Why an object's gateway key cannot be used now, beside its [Unavailable] badge (admin design §5.5.2, §5.5.4; B-44). */
export const KEY_UNAVAILABLE_MESSAGES: Record<string, string> = {
  missing: 'The gateway key is not known to the platform.',
  disabled: 'The gateway key is disabled.',
  expired: 'The gateway key has expired.',
  revoked: 'The gateway key has been revoked.',
  not_active: 'The gateway key is not active.',
  owner_inactive: 'The team or department of the gateway key is inactive.',
  unusable: 'The gateway key allows no residency in common with the platform’s key.',
  budget_key: 'The key’s budget is used up.',
  budget_team: 'The team’s budget is used up.',
  budget_department: 'The department’s budget is used up.',
}

/** The cost views' sentences (admin design §8.6). */
export const COST_MESSAGES = {
  estimatedNote: (calls: string) => `Estimated: ${calls} calls had no cost from the gateway and are priced from the model list.`,
  gatewayRecord: 'The gateway’s figures are the record for internal charging; they also count retries and fallbacks.',
  empty: 'No model calls in this period.',
  ownDepartmentOnly: 'Only the department that owns the knowledge base sees its cost.',
  gatewayFigures: (cost: string, requests: string) => `The AI gateway counted ${cost} in ${requests} requests for the key.`,
  sharedKey: 'The gateway’s figures are the key’s: when other assistants or knowledge bases share the key, theirs are included.',
} as const

/** When the problem has no code we know: by status. */
export const STATUS_MESSAGES = {
  offline: 'The server cannot be reached. Check your connection and try again.',
  unknown: 'Something went wrong. Try again.',
  401: 'Your session has expired. Sign in again.',
  403: 'You do not have permission to do that.',
  404: 'It no longer exists. Reload the page.',
  409: 'The change conflicts with something that already exists.',
  429: 'Too many requests. Wait a moment and try again.',
  server: 'The server had a problem. Try again in a moment.',
  rejected: 'The request was refused. Check what you have filled in.',
} as const

/** Fills `{name}` placeholders; a missing parameter leaves its placeholder out. */
export function formatMessage(template: string, params?: Record<string, unknown>): string {
  return template.replace(/\{(\w+)\}/g, (_, name: string) => (params?.[name] === undefined ? '' : String(params[name])))
}

/**
 * A `platform_rule` refusal's text by its `constraint`, where the generic one would name the constraint raw and point at
 * fields the refusal does not mark.
 */
export const PLATFORM_RULE_MESSAGES: Record<string, string> = {
  limits_from_gateway: 'The AI gateway now sends this model’s limits, so they can no longer be set here. Reload the page to see them.',
}

/**
 * Why a test sign-in failed (identity design §19; the codes are SETUP_TEST_REASONS in setup/setupValues.ts): the step that
 * failed and what to do. An unknown code gets `setupTestMessage`'s generic text, naming the code.
 */
export const SETUP_TEST_MESSAGES: Record<string, string> = {
  authority_invalid:
    'The provider address is not valid. Enter the full https address of the provider, without a query or #, for example https://login.example.org/realms/ume.',
  authority_unreachable:
    'kalla-admin could not reach the provider address. Check the address, and that kalla-admin can reach the provider through the network and any proxy. On a Docker Compose host with the host firewall, kalla-admin reaches only the provider the deployment names: run init-deployment.sh again with --oidc-authority and this address, then the host firewall.',
  tls: 'kalla-admin does not trust the provider’s certificate. Add the provider’s certificate authority to kalla-admin’s trusted certificates, or check the address.',
  redirected:
    'The provider address answered with a redirect, and kalla-admin follows none. Enter the address the provider names as its issuer, exactly.',
  not_discovery:
    'The provider address does not lead to a sign-in discovery document. Check the address: for Keycloak it ends with /realms/ and the realm, for Entra ID with /v2.0.',
  issuer_mismatch:
    'The provider names itself with another address (its issuer). Enter exactly the issuer address the provider names, including any trailing slash.',
  insecure_endpoint: 'The provider lists an address that is not https, and kalla-admin signs in over https only. Fix the provider’s public address.',
  par_required:
    'The provider requires pushed authorization requests, which kalla-admin does not make. Turn that requirement off for this client at the provider.',
  keys_unavailable: 'kalla-admin could not fetch the provider’s signing keys. Check that the provider publishes its keys and that kalla-admin can reach them.',
  stored_secret_unreadable: 'The stored client secret can no longer be read. Type the client secret again.',
  provider_error:
    'The provider sent you back with an error instead of signing you in. Check that the redirect URI is registered for this client, that the client is enabled and that you may use it. Then test again.',
  token_refused: 'The provider refused kalla-admin’s sign-in request. Most often the client secret or the client ID is wrong. Check both and test again.',
  token_endpoint_unreachable:
    'kalla-admin could not reach the provider’s token address. Check that kalla-admin can reach the provider through the network and any proxy.',
  id_token_missing: 'The provider sent no ID token. Check that the client is an OpenID Connect client that allows the openid scope.',
  id_token_invalid:
    'The provider’s ID token did not pass the checks of its signature, issuer, audience (the client ID) or lifetime. Check the client ID, and that the clocks of kalla-admin and the provider are right.',
  nonce_mismatch: 'The provider’s answer did not belong to this test. Test again.',
  secret_required:
    'You changed the provider address or the client ID, so the stored client secret is not sent to it. Type the client secret of this client.',
  deployment_secret_without_authority:
    'The deployment sets the client secret but not the provider address, so that secret is not sent to an address typed here. Set the provider address in the deployment too, or remove the deployment’s secret.',
  provider_url_invalid:
    'The provider address is not valid. Enter the full https address of the provider’s discovery document, without a query or #, for example https://login.example.org/realms/ume/.well-known/openid-configuration.',
  client_refused: 'The provider refused this client ID or client secret. Check both at the provider and check again.',
  client_unverified: 'The provider’s answer did not say whether it accepts this client ID and client secret.',
  browser_mismatch: 'The sign-in finished in another browser than the one that started the test. Test again, and sign in in the window the test opens.',
  session_expired: 'Your setup session ended before the sign-in finished. Enter the setup code again, then test again.',
  unknown_test: 'The test is unknown, already used or expired. Test again.',
  wrong_tenant: 'The account does not belong to the tenant under Tenant ID. Check the tenant ID, or sign in with an account of that tenant.',
  guest_refused: 'The account is a guest account, and guests are refused. Sign in with an account of your own organisation, or turn off Refuse guests.',
}

/** A test reason's text; an unknown code gets a generic text that names it. */
export function setupTestMessage(reason: string): string {
  return SETUP_TEST_MESSAGES[reason] ?? `The test sign-in failed (${reason}). Check the values and test again.`
}

/** Why `POST /bff/setup/complete` refused to save (`setup_test_required`; SETUP_COMPLETE_REASONS in setup/setupValues.ts). */
export const SETUP_COMPLETE_MESSAGES: Record<string, string> = {
  unknown_test: 'The test is unknown to this setup session. Test sign-in again, then finish.',
  not_latest: 'A newer test was started. Wait for its result, then finish.',
  not_passed: 'The latest test sign-in did not pass. Test again.',
  not_platform_admin: 'The person who tested is not a platform admin. Give them the role rag-platform-admin at the provider and test again.',
  values_changed: 'The values differ from the ones tested. Test sign-in again with these values.',
  admin_test_required:
    'Open WebUI’s values are saved only after a passed test sign-in by a platform admin in this setup session. Go to the Sign-in step and test sign-in first.',
}

/** A completion refusal's text; an unknown code gets a generic text that names it. */
export function setupCompleteMessage(reason: string): string {
  return SETUP_COMPLETE_MESSAGES[reason] ?? `Saving needs a passed test sign-in by a platform admin (${reason}). Test sign-in again.`
}

/**
 * Open WebUI's step (admin design §5.11 step 4) checks instead of signing in: where a reason of SETUP_TEST_REASONS or
 * SETUP_COMPLETE_REASONS needs other words there (the address is a discovery document's, and the step has a Check button and
 * Save, not Test sign-in and Finish), its text; every other reason keeps its text above.
 */
export const OPENWEBUI_CHECK_MESSAGES: Record<string, string> = {
  redirected:
    'The provider address answered with a redirect, and kalla-admin follows none. Enter the discovery document’s address exactly: the issuer followed by /.well-known/openid-configuration.',
  not_discovery:
    'The provider address does not lead to a sign-in discovery document. Check the address: it ends with /.well-known/openid-configuration.',
  issuer_mismatch:
    'The discovery document names another issuer than the address it was fetched from. Enter the issuer the provider names, followed by /.well-known/openid-configuration.',
  unknown_test: 'The check is unknown to this setup session. Check again, then save.',
  not_latest: 'A newer check was made. Save after the newest one.',
  not_passed: 'The latest check did not pass. Check again.',
  values_changed: 'The values differ from the ones checked. Check again with these values.',
}

/** A reason's text in Open WebUI's step: its own wording where it has one, else the test's or the completion's. */
export function openWebUiCheckMessage(reason: string): string {
  return OPENWEBUI_CHECK_MESSAGES[reason] ?? SETUP_TEST_MESSAGES[reason] ?? setupCompleteMessage(reason)
}
