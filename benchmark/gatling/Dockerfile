# Aucune image Docker officielle largement adoptee pour Gatling OSS (contrairement a
# grafana/k6) : cette image telecharge le bundle OSS directement depuis Maven Central sur une
# base JDK. Constat reel important : depuis la version 3.15.x, le bundle OSS n'est plus un
# script bin/gatling.sh autonome mais un squelette de projet Maven (pom.xml + mvnw +
# src/test/java), pilote par le gatling-maven-plugin — d'ou l'usage de mvnw ci-dessous plutot
# qu'un appel direct a gatling.sh. Voir benchmark/README.md pour la commande d'usage complete.

FROM eclipse-temurin:21-jdk

ARG GATLING_VERSION=3.15.1

# 1001 et non 1000 : la base est passee a Ubuntu 26.04, ou l'UID 1000 est deja pris par
# l'utilisateur "ubuntu" — useradd echouerait. Verifie sur l'image, pas suppose.
ARG GATLING_UID=1001

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl unzip \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt
RUN curl -fsSL -o gatling.zip \
      "https://repo1.maven.org/maven2/io/gatling/highcharts/gatling-charts-highcharts-bundle/${GATLING_VERSION}/gatling-charts-highcharts-bundle-${GATLING_VERSION}-bundle.zip" \
    && unzip -q gatling.zip \
    && rm gatling.zip \
    && mv "gatling-charts-highcharts-bundle-${GATLING_VERSION}" gatling \
    && rm -f gatling/src/test/java/example/BasicSimulation.java

# Contrairement aux trois images .NET, celle-ci ecrit reellement dans son repertoire de travail :
# mvnw y compile la simulation sous target/, et telecharge son depot Maven sous $HOME/.m2. Il ne
# suffit donc pas de basculer d'utilisateur, il faut lui donner les deux emplacements.
#
# Le mkdir de target/gatling n'est pas decoratif, et c'est un echec reel qui l'a impose : ce chemin
# est le point de montage du dossier de resultats (voir benchmark/run.sh), et Docker cree les
# repertoires manquants d'un montage a l'execution, en root. Sans ce mkdir au build, /opt/gatling/
# target appartenait donc a root pendant le tir, et Maven echouait sur "Cannot create resource
# output directory: /opt/gatling/target/test-classes" — un chown du seul /opt/gatling ne pouvait
# pas le prevoir, puisque target/ n'existe pas encore a ce moment-la.
RUN useradd --uid "${GATLING_UID}" --create-home --shell /bin/sh gatling \
    && mkdir -p /opt/gatling/target/gatling \
    && chown -R "${GATLING_UID}:${GATLING_UID}" /opt/gatling

USER ${GATLING_UID}
WORKDIR /opt/gatling
ENTRYPOINT ["./mvnw", "-B", "-q", "gatling:test"]
