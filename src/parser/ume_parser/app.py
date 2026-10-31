"""The FastAPI application (parser design §7.1, §10.1). `uvicorn ume_parser.app:app` or `python -m ume_parser`.

The API process serves requests and runs the supervisor; Docling and the NER model run only in worker processes.
"""

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI

from ume_parser import api, health, info
from ume_parser.settings import NerSettings, Settings
from ume_parser.supervisor import Supervisor


def create_app(
    settings: Settings | None = None, supervisor: Supervisor | None = None, ner_settings: NerSettings | None = None
) -> FastAPI:
    settings = settings or Settings()
    ner_settings = ner_settings or NerSettings()
    supervisor = supervisor or Supervisor(settings, ner_settings)

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        app.state.static_info = info.collect(settings, ner_settings)
        app.state.build_hash = info.build_hash(app.state.static_info)
        app.state.build = info.build(app.state.static_info, app.state.build_hash)
        await supervisor.start()
        try:
            yield
        finally:
            await supervisor.stop()

    app = FastAPI(title="ume-parser", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
    app.state.settings = settings
    app.state.supervisor = supervisor
    app.include_router(health.router)
    app.include_router(info.router)
    app.include_router(api.router)
    return app


app = create_app()
