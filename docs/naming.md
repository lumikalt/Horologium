# Naming Convention

The codebase uses a horological metaphor for domain types throughout. Match it when adding code.

| Thing                   | Name       | Rationale                                                        |
|-------------------------|------------|-------------------------------------------------------------------|
| Project                 | Horologium | The instrument that models time and mechanical motion            |
| Core                    | Orrery     | The clockwork engine at the center                               |
| Unit / Resource         | Gear       | A discrete mechanical part that meshes with others               |
| Port                    | Arbor      | The shaft that transmits motion between gears                    |
| Scheduler               | Escapement | The mechanism that releases energy in discrete, controlled steps |
| Pipeline Topology       | Train      | An arranged sequence of gears transmitting motion                |
| ISA Plugin              | Mechanism  | The specific mechanical logic governing a Train                  |
| Instruction Token       | Tooth      | What one gear passes to the next; the discrete unit of transfer  |
| µop                     | Impulse    | The single discrete push the Escapement delivers per tick        |
| Statistics              | Dial       | The readable face of the instrument                              |
| Configuration Parameter | Setting    | The instrument's configuration before it runs                    |
| One Simulation Run      | Revolution | One full turning of the mechanism                                |
