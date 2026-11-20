"""Token-count parity golden for the BGE-M3 counter (processing design §6.1, to-verify #26).

Writes passages.jsonl: 1,000 Swedish and English passages with the token count of Hugging Face's tokenizer for
BAAI/bge-m3 at revision 5617a9f61b028005a4858fdac845db406aefb181 (tokenizer.json, add_special_tokens=False).
Run once, by hand, from an isolated environment:

    python3 -m venv venv && venv/bin/pip install tokenizers==0.22.1
    curl -L -o tokenizer.json https://huggingface.co/BAAI/bge-m3/resolve/5617a9f61b028005a4858fdac845db406aefb181/tokenizer.json
    venv/bin/python -I generate.py tokenizer.json ../../../../src/Kalla.Retrieval/Text/sv_SE/sv_SE.dic

Passages: 500 seeded word sequences from the sv_SE word list (compounds, inflections), 300 windows over the prose below,
200 edge cases (numbers, dates, personnummer shapes, URLs, e-mail, punctuation, NBSP and other spaces, ligatures,
decomposed å/ä/ö, emoji, mixed case, long words, repeated whitespace).
"""
import json
import random
import sys

from tokenizers import Tokenizer

PROSE_SV = """Umeå kommun ansvarar för förskola, grundskola och gymnasieskola. Ansökan om bygglov görs via e-tjänsten.
Handläggningstiden är normalt tio veckor från det att ansökan är komplett. Avgiften för bygglov beräknas enligt taxan
som kommunfullmäktige har beslutat. Kontakta kontaktcenter på telefon 090-16 10 00 om du har frågor. Föreskrifterna
gäller från den 1 januari 2026. Enligt 3 kap. 4 § plan- och bygglagen ska byggnadsnämnden pröva ansökan. Protokollet
justerades den 15 januari. Bilaga 2 innehåller kartor över detaljplaneområdet. Barnomsorgsavgiften är högst 1 688
kronor per månad. Snöröjningen prioriterar huvudgator, gång- och cykelvägar samt busshållplatser. Återvinningscentralen
har öppet måndag till fredag. Ekonomiskt bistånd söks hos socialtjänsten. Förvaltningen för teknik och fastighet sköter
kommunens lokaler. Kommun- och regionförbundet samordnar EU-medel för landsbygdsutveckling."""

PROSE_EN = """Umeå municipality is responsible for preschool, compulsory school and upper secondary school. Building permits are
applied for through the e-service. The processing time is normally ten weeks from a complete application. Contact the
customer centre if you have questions. The regulations apply from 1 January 2026. Appendix 2 contains maps of the
area. Snow clearing gives priority to main streets, cycle paths and bus stops. The recycling centre is open Monday to
Friday. Financial assistance is applied for at the social services."""

EDGE = [
    "19121212-1212", "121212+1212", "0701234567", "+46 (0)90-16 10 00", "903 25 Umeå", "1 234 567 kr", "2024-05-12",
    "https://www.umea.se/kommun-och-politik/protokoll/2024/kf.html", "kontakt@umea.se", "anna.andersson[at]umea.se",
    "§ 4", "§4", "3 kap. 4 §", "t.ex. bl.a. m.m. s.k. dvs.", "ÅÄÖ åäö", "åäö", "ﬁnans ﬂöde",
    "kommun och region ", "text­brytning", "zero​width", "Emoji 😀 och 👍🏽", "ALLA VERSALER HÄR",
    "Bygglovsansökan", "detaljplaneområdesgränsen", "     ledande blanksteg", "slut    blanksteg    ", "rad\nbrytning\n\nstycke",
    "tab\tseparerad\ttext", "Kolumn: värde; Kolumn: värde", "**fet** och _kursiv_", "```\nkod\n```", "100 %", "50–60 procent",
    "«citat» ”citat” 'citat'", "e-post: info@example.com", "Tel. 090-16 00 00", "Org.nr 212000-2627", "Bankgiro 123-4567",
    "Kapitel 3", "1.2.3 Avgifter", "IV. Bilagor", "Sida 3 av 12", "Kommun › Förvaltningar › Teknik", "…", "—", "x" * 300,
]


def main():
    tokenizer = Tokenizer.from_file(sys.argv[1])
    words = []
    with open(sys.argv[2], encoding="utf-8") as dic:
        next(dic)
        for line in dic:
            word = line.strip().split("/")[0]
            if word and word.isalpha():
                words.append(word)
    rng = random.Random(26)
    passages = []
    for _ in range(500):
        n = rng.randint(3, 120)
        text = " ".join(rng.choice(words) for _ in range(n))
        if rng.random() < 0.5:
            text = text[0].upper() + text[1:] + "."
        passages.append(text)
    prose = (PROSE_SV + "\n" + PROSE_EN).replace("\n", " ")
    for _ in range(300):
        start = rng.randint(0, len(prose) - 50)
        length = rng.randint(20, 900)
        passages.append(prose[start:start + length])
    while len(passages) < 1000:
        k = rng.randint(1, 4)
        passages.append(" ".join(rng.choice(EDGE) for _ in range(k)))
    with open("passages.jsonl", "w", encoding="utf-8", newline="\n") as out:
        for text in passages:
            count = len(tokenizer.encode(text, add_special_tokens=False).ids)
            out.write(json.dumps({"text": text, "count": count}, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
