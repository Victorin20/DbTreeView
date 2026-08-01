window.dbTreeView = (() => {
    let graph = null;
    let selectedNode = null;

    const kindLabels = {
        Procedure: "Procédure stockée",
        View: "Vue",
        Table: "Table",
        Function: "Fonction",
        External: "Référence non résolue",
        Schema: "Schéma"
    };

    function render(elementId, map, options) {
        const container = document.getElementById(elementId);
        if (!container) {
            throw new Error("La zone du graphe est introuvable.");
        }

        if (typeof cytoscape === "undefined") {
            throw new Error("Cytoscape.js n’est pas chargé. Vérifie la connexion Internet ou installe la bibliothèque localement.");
        }

        destroy();

        const nodeElements = map.nodes.map(node => ({
            data: {
                id: node.id,
                label: node.name,
                qualifiedName: node.qualifiedName,
                schema: node.schema,
                kind: node.kind,
                resolved: node.isResolved,
                parent: node.parentId || undefined
            }
        }));

        const edgeElements = map.edges.map(edge => ({
            data: {
                id: edge.id,
                source: edge.sourceId,
                target: edge.targetId,
                kind: edge.kind,
                label: edge.label
            },
            classes: edge.kind === "ForeignKey" && !options.showForeignKeys ? "foreign-key-hidden" : ""
        }));

        graph = cytoscape({
            container,
            elements: [...nodeElements, ...edgeElements],
            minZoom: 0.18,
            maxZoom: 2.4,
            selectionType: "single",
            boxSelectionEnabled: false,
            style: [
                {
                    selector: "node",
                    style: {
                        "width": 154,
                        "height": 45,
                        "shape": "round-rectangle",
                        "background-color": "#18202c",
                        "border-width": 1.5,
                        "border-color": "#4a5667",
                        "label": "data(label)",
                        "color": "#e9edf3",
                        "font-family": "Inter, Segoe UI, sans-serif",
                        "font-size": 11,
                        "font-weight": 600,
                        "text-wrap": "ellipsis",
                        "text-max-width": 132,
                        "text-valign": "center",
                        "text-halign": "center",
                        "overlay-opacity": 0,
                        "transition-property": "opacity, border-color, border-width, background-color",
                        "transition-duration": "180ms"
                    }
                },
                {
                    selector: "node[kind = 'Procedure']",
                    style: { "background-color": "#122c32", "border-color": "#39d0c3" }
                },
                {
                    selector: "node[kind = 'View']",
                    style: { "background-color": "#282241", "border-color": "#a991ff" }
                },
                {
                    selector: "node[kind = 'Table']",
                    style: { "background-color": "#172b43", "border-color": "#5aa9ff" }
                },
                {
                    selector: "node[kind = 'Function']",
                    style: { "background-color": "#3a2b19", "border-color": "#f5ad55" }
                },
                {
                    selector: "node[kind = 'External']",
                    style: {
                        "background-color": "#272a31",
                        "border-color": "#808998",
                        "border-style": "dashed",
                        "color": "#aab1bc"
                    }
                },
                {
                    selector: "node[kind = 'Schema']",
                    style: {
                        "shape": "round-rectangle",
                        "background-color": "#111722",
                        "background-opacity": 0.38,
                        "border-width": 1,
                        "border-color": "#303948",
                        "border-style": "dashed",
                        "padding": 30,
                        "label": "data(label)",
                        "font-size": 10,
                        "color": "#758096",
                        "text-valign": "top",
                        "text-halign": "left",
                        "text-margin-x": 8,
                        "text-margin-y": 8
                    }
                },
                {
                    selector: "edge",
                    style: {
                        "width": 1.4,
                        "line-color": "#526175",
                        "target-arrow-color": "#8090a7",
                        "target-arrow-shape": "triangle",
                        "arrow-scale": 0.8,
                        "curve-style": "bezier",
                        "opacity": 0.76,
                        "overlay-opacity": 0,
                        "transition-property": "opacity, width, line-color",
                        "transition-duration": "180ms"
                    }
                },
                {
                    selector: "edge[kind = 'ForeignKey']",
                    style: {
                        "line-color": "#5e6c80",
                        "target-arrow-color": "#5e6c80",
                        "line-style": "dashed",
                        "opacity": 0.52
                    }
                },
                { selector: ".foreign-key-hidden", style: { "display": "none" } },
                { selector: ".dimmed", style: { "opacity": 0.09 } },
                {
                    selector: "node.highlighted",
                    style: {
                        "opacity": 1,
                        "border-width": 3,
                        "border-color": "#f7df8a",
                        "z-index": 9999
                    }
                },
                { selector: "edge.contextual", style: { "opacity": 1, "width": 2.6, "line-color": "#9eb1cb" } },
                {
                    selector: "node:selected",
                    style: {
                        "border-width": 3,
                        "border-color": "#ffffff",
                        "background-blacken": -0.08
                    }
                }
            ],
            layout: layoutOptions("cose")
        });

        graph.on("tap", "node", event => selectNode(event.target));
        graph.on("tap", event => {
            if (event.target === graph) {
                resetFocus();
            }
        });

        const focusButton = document.getElementById("selection-focus");
        const resetButton = document.getElementById("selection-reset");
        if (focusButton) focusButton.onclick = focusSelection;
        if (resetButton) resetButton.onclick = resetFocus;

        graph.ready(() => graph.fit(undefined, 42));
    }

    function layoutOptions(name) {
        if (name === "breadthfirst") {
            return {
                name: "breadthfirst",
                directed: true,
                padding: 50,
                spacingFactor: 1.18,
                avoidOverlap: true,
                animate: false
            };
        }

        if (name === "circle") {
            return { name: "circle", padding: 50, spacingFactor: 1.1, avoidOverlap: true, animate: false };
        }

        return {
            name: "cose",
            padding: 55,
            animate: false,
            randomize: true,
            nodeRepulsion: 7600,
            idealEdgeLength: 110,
            edgeElasticity: 90,
            gravity: 0.22,
            nestingFactor: 1.15,
            componentSpacing: 90,
            nodeOverlap: 28,
            numIter: 900
        };
    }

    function selectNode(node) {
        if (!graph || node.data("kind") === "Schema") return;

        selectedNode = node;
        const incoming = node.incomers("edge[kind = 'DependsOn']").length;
        const outgoing = node.outgoers("edge[kind = 'DependsOn']").length;

        setText("selection-name", node.data("label"));
        setText("selection-qualified", node.data("qualifiedName"));
        setText("selection-kind", kindLabels[node.data("kind")] || node.data("kind"));
        setText("selection-incoming", incoming.toString());
        setText("selection-outgoing", outgoing.toString());

        const kindBadge = document.getElementById("selection-kind");
        if (kindBadge) kindBadge.dataset.kind = node.data("kind").toLowerCase();

        const focusButton = document.getElementById("selection-focus");
        if (focusButton) focusButton.disabled = false;

        highlightElements(node.closedNeighborhood().union(node.ancestors()), node);
    }

    function focusSelection() {
        if (!graph || !selectedNode) return;
        const neighborhood = selectedNode.closedNeighborhood().union(selectedNode.ancestors());
        graph.fit(neighborhood, 90);
    }

    function resetFocus() {
        if (!graph) return;
        selectedNode = null;
        graph.elements().removeClass("dimmed highlighted contextual");
        graph.nodes().unselect();
        setText("selection-name", "Sélectionne un objet");
        setText("selection-qualified", "Clique sur un nœud pour afficher ses relations.");
        setText("selection-kind", "—");
        setText("selection-incoming", "—");
        setText("selection-outgoing", "—");
        const focusButton = document.getElementById("selection-focus");
        if (focusButton) focusButton.disabled = true;
    }

    function highlightElements(context, highlighted) {
        graph.elements().addClass("dimmed").removeClass("highlighted contextual");
        context.removeClass("dimmed");
        context.edges().addClass("contextual");
        highlighted.addClass("highlighted");
    }

    function search(value) {
        if (!graph) return;
        const term = (value || "").trim().toLocaleLowerCase("fr");
        resetFocus();
        if (!term) return;

        const matches = graph.nodes().filter(node =>
            node.data("kind") !== "Schema" &&
            String(node.data("qualifiedName")).toLocaleLowerCase("fr").includes(term));

        if (matches.empty()) {
            graph.elements().addClass("dimmed");
            return;
        }

        const context = matches.closedNeighborhood().union(matches.ancestors());
        highlightElements(context, matches);
        graph.fit(context, 80);
    }

    function changeLayout(name) {
        if (!graph) return;
        graph.layout(layoutOptions(name)).run();
        graph.fit(undefined, 45);
    }

    function showForeignKeys(show) {
        if (!graph) return;
        graph.edges("[kind = 'ForeignKey']").toggleClass("foreign-key-hidden", !show);
    }

    function fit() {
        if (graph) graph.fit(undefined, 45);
    }

    async function toggleFullscreen(elementId) {
        const panel = document.getElementById(elementId);
        if (!panel) return;

        if (document.fullscreenElement === panel) {
            await document.exitFullscreen();
        } else {
            await panel.requestFullscreen();
        }
    }

    function refreshAfterFullscreenChange() {
        const button = document.getElementById("graph-fullscreen");
        const panel = document.getElementById("graph-panel");
        const isFullscreen = document.fullscreenElement === panel;

        if (button) {
            button.textContent = isFullscreen ? "↙ Quitter le plein écran" : "⛶ Plein écran";
            button.classList.toggle("active", isFullscreen);
            button.setAttribute("aria-pressed", String(isFullscreen));
        }

        if (graph) {
            window.requestAnimationFrame(() => {
                graph.resize();
                graph.fit(undefined, 45);
            });
        }
    }

    document.addEventListener("fullscreenchange", refreshAfterFullscreenChange);

    function setText(id, value) {
        const element = document.getElementById(id);
        if (element) element.textContent = value;
    }

    function destroy() {
        if (graph) {
            graph.destroy();
            graph = null;
        }
        selectedNode = null;
    }

    return { render, search, changeLayout, showForeignKeys, fit, toggleFullscreen, destroy };
})();
